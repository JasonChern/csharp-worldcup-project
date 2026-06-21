# WorldCup Live Hub — 架構設計文件

> 世足賽事即時資訊平台。練習目標：**.NET 微服務架構**、**MSSQL + Stored Procedure**、**Publisher/Subscriber（RabbitMQ + MassTransit）**、**React 前端 + SignalR 即時推送**。

---

## 1. 系統總覽

```
                                  ┌──────────────────────────────┐
                                  │         React (Vite + TS)     │
                                  │  賽事看板 / 訂閱中心 / 通知     │
                                  └───────┬───────────────▲───────┘
                                   REST   │               │ SignalR (WebSocket)
                                          ▼               │
                                  ┌──────────────────────────────┐
                                  │      API Gateway (YARP)       │
                                  └───┬──────────┬──────────┬─────┘
                                      ▼          ▼          ▼
              ┌────────────────┐ ┌──────────────────┐ ┌────────────────────┐
              │  Match Service │ │ Subscription Svc │ │ Live Score Service  │
              │  (Publisher)   │ │  (MSSQL + SP)    │ │ (Subscriber+SignalR)│
              └───────┬────────┘ └──────────────────┘ └─────────▲──────────┘
                      │ Outbox                                   │ consume
                      ▼                                          │
              ┌────────────────┐                                 │
              │ MatchDb (MSSQL) │                                 │
              │  + Outbox table │                                 │
              └───────┬─────────┘                                 │
                      │ OutboxPublisher (background)              │
                      ▼                                           │
              ┌─────────────────────────────────────────────────────────────┐
              │                    RabbitMQ (MassTransit)                     │
              │   exchange: match-events   routing: goal / card / status      │
              └───────┬─────────────────────────────────────────────▲────────┘
                      ▼                                              │
              ┌────────────────────┐                                │
              │ Notification Service│────────────────────────────────┘
              │   (Subscriber)      │  produce 個人化通知
              └────────────────────┘
```

### 微服務清單

| 服務 | 角色 | 資料庫 | 重點 |
|------|------|--------|------|
| **Ingestion Job** | 資料來源 | (寫入 MatchDb) | 定時從外部網站擷取 deadball / live 資料 |
| **Match Service** | Event Publisher | MatchDb | 賽程/比分管理、記錄事件、Outbox |
| **Subscription Service** | REST | SubscriptionDb | 使用者訂閱的球隊/賽事 |
| **Live Score Service** | Subscriber | (讀模型 in-memory / Redis 可選) | 即時比分讀模型、SignalR Hub |
| **Notification Service** | Subscriber | NotificationDb | 依訂閱產生個人化通知 |
| **API Gateway** | 路由 | — | 前端統一入口（YARP） |

> 原則：**Database per Service**。各服務不共用資料表，跨服務只靠事件溝通。

---

### 資料流（含 Ingestion Job）

```
[外部世足資料網站]
        │  HTTP 擷取（定時 / 輪詢）
        ▼
┌──────────────────────┐     deadball：賽程、隊伍、球員、最終比分（低頻，例如每數分鐘/每日）
│   Ingestion Job       │     live：進行中事件、即時比分（高頻，例如每 5~15 秒）
│  (Worker Service)     │
└──────────┬───────────┘
           │ 經由 Match Service API（或直接呼叫 SP）寫入
           ▼
      [Match Service] ──► MatchDb ──► Outbox ──► RabbitMQ ──► 下游服務
```

> 設計重點：Ingestion Job **不直接碰下游**，只負責「把外部資料正規化後交給 Match Service」。Match Service 仍是唯一的事件來源（single source of truth），保持 Pub/Sub 鏈路單純。

---

## 2. 技術棧

- **後端**：.NET 8、ASP.NET Core Minimal API、Dapper（呼叫 SP 最直觀）
- **資料庫**：SQL Server 2022（docker）、Stored Procedure
- **訊息佇列**：RabbitMQ + MassTransit
- **即時推送**：SignalR
- **前端**：React + Vite + TypeScript + TanStack Query + @microsoft/signalr
- **容器**：docker-compose 一鍵啟動

---

## 3. 事件契約（Event Contracts）

放在共用專案 `Contracts`（class library，被各服務參考）。

```csharp
namespace WorldCup.Contracts.Events;

public record MatchStarted(Guid MatchId, Guid HomeTeamId, Guid AwayTeamId, DateTime KickoffUtc);

public record GoalScored(
    Guid MatchId,
    Guid TeamId,
    Guid PlayerId,
    int Minute,
    int HomeScore,
    int AwayScore,
    DateTime OccurredUtc);

public record CardIssued(
    Guid MatchId,
    Guid TeamId,
    Guid PlayerId,
    string CardType,   // "Yellow" | "Red"
    int Minute,
    DateTime OccurredUtc);

public record MatchEnded(Guid MatchId, int HomeScore, int AwayScore, DateTime OccurredUtc);
```

**RabbitMQ 拓樸（MassTransit 自動建立）**
- Exchange：依事件型別自動分 exchange（MassTransit 慣例）
- 各 Subscriber 用獨立 queue 綁定，互不影響（fan-out 給多個 consumer）

---

## 4. 資料庫設計（MatchDb）

### 4.1 資料表

```sql
CREATE TABLE Teams (
    TeamId      UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    ExternalId  NVARCHAR(64)  NULL UNIQUE,       -- 外部來源對應 ID（Ingestion 用）
    Name        NVARCHAR(100) NOT NULL,
    GroupCode   CHAR(1)       NOT NULL          -- A~H 小組
);

CREATE TABLE Players (
    PlayerId    UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    TeamId      UNIQUEIDENTIFIER NOT NULL REFERENCES Teams(TeamId),
    Name        NVARCHAR(100) NOT NULL,
    JerseyNo    INT NOT NULL
);

CREATE TABLE Matches (
    MatchId     UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    ExternalId  NVARCHAR(64)  NULL UNIQUE,       -- 外部來源對應 ID（Ingestion 用）
    HomeTeamId  UNIQUEIDENTIFIER NOT NULL REFERENCES Teams(TeamId),
    AwayTeamId  UNIQUEIDENTIFIER NOT NULL REFERENCES Teams(TeamId),
    KickoffUtc  DATETIME2 NOT NULL,
    Status      VARCHAR(20) NOT NULL DEFAULT 'Scheduled', -- Scheduled/Live/Ended
    HomeScore   INT NOT NULL DEFAULT 0,
    AwayScore   INT NOT NULL DEFAULT 0
);

CREATE TABLE MatchEvents (
    EventId     BIGINT IDENTITY PRIMARY KEY,
    ExternalEventId NVARCHAR(64) NULL UNIQUE, -- 外部事件 ID，做 live 去重用
    MatchId     UNIQUEIDENTIFIER NOT NULL REFERENCES Matches(MatchId),
    EventType   VARCHAR(30) NOT NULL,        -- Goal/YellowCard/RedCard/Start/End
    TeamId      UNIQUEIDENTIFIER NULL,
    PlayerId    UNIQUEIDENTIFIER NULL,
    Minute      INT NULL,
    OccurredUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Outbox Pattern：保證 DB 寫入與發訊息一致
CREATE TABLE OutboxMessages (
    OutboxId    BIGINT IDENTITY PRIMARY KEY,
    EventType   VARCHAR(100) NOT NULL,       -- 對應 Contracts 型別全名
    Payload     NVARCHAR(MAX) NOT NULL,      -- JSON
    CreatedUtc  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ProcessedUtc DATETIME2 NULL              -- NULL = 尚未發送
);
CREATE INDEX IX_Outbox_Unprocessed ON OutboxMessages(OutboxId) WHERE ProcessedUtc IS NULL;
```

### 4.2 Stored Procedures（核心練習點）

**(a) 記錄進球 — 交易內同時更新比分、寫事件、寫 Outbox**

```sql
CREATE OR ALTER PROCEDURE dbo.sp_RecordGoal
    @MatchId  UNIQUEIDENTIFIER,
    @TeamId   UNIQUEIDENTIFIER,
    @PlayerId UNIQUEIDENTIFIER,
    @Minute   INT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRAN;

        -- 1. 更新比分（依進球隊伍判斷主/客）
        UPDATE Matches
        SET HomeScore = HomeScore + CASE WHEN HomeTeamId = @TeamId THEN 1 ELSE 0 END,
            AwayScore = AwayScore + CASE WHEN AwayTeamId = @TeamId THEN 1 ELSE 0 END
        WHERE MatchId = @MatchId AND Status = 'Live';

        IF @@ROWCOUNT = 0
            THROW 50001, 'Match not found or not live', 1;

        -- 2. 寫事件
        INSERT INTO MatchEvents(MatchId, EventType, TeamId, PlayerId, Minute)
        VALUES (@MatchId, 'Goal', @TeamId, @PlayerId, @Minute);

        -- 3. 取得最新比分，組 Outbox payload
        DECLARE @Home INT, @Away INT;
        SELECT @Home = HomeScore, @Away = AwayScore FROM Matches WHERE MatchId = @MatchId;

        INSERT INTO OutboxMessages(EventType, Payload)
        VALUES (
            'WorldCup.Contracts.Events.GoalScored',
            (SELECT @MatchId AS MatchId, @TeamId AS TeamId, @PlayerId AS PlayerId,
                    @Minute AS Minute, @Home AS HomeScore, @Away AS AwayScore,
                    SYSUTCDATETIME() AS OccurredUtc
             FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
        );

        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END
```

**(b) 計算小組積分榜 — 展示 CTE / 視窗函數**

```sql
CREATE OR ALTER PROCEDURE dbo.sp_GetGroupStandings
    @GroupCode CHAR(1)
AS
BEGIN
    SET NOCOUNT ON;
    WITH Results AS (
        SELECT HomeTeamId AS TeamId,
               CASE WHEN HomeScore > AwayScore THEN 3 WHEN HomeScore = AwayScore THEN 1 ELSE 0 END AS Pts,
               HomeScore AS GF, AwayScore AS GA
        FROM Matches WHERE Status = 'Ended'
        UNION ALL
        SELECT AwayTeamId,
               CASE WHEN AwayScore > HomeScore THEN 3 WHEN AwayScore = HomeScore THEN 1 ELSE 0 END,
               AwayScore, HomeScore
        FROM Matches WHERE Status = 'Ended'
    ),
    Agg AS (
        SELECT t.TeamId, t.Name,
               SUM(r.Pts) AS Points,
               SUM(r.GF) - SUM(r.GA) AS GoalDiff,
               SUM(r.GF) AS GoalsFor
        FROM Teams t LEFT JOIN Results r ON r.TeamId = t.TeamId
        WHERE t.GroupCode = @GroupCode
        GROUP BY t.TeamId, t.Name
    )
    SELECT *,
           RANK() OVER (ORDER BY Points DESC, GoalDiff DESC, GoalsFor DESC) AS Rank
    FROM Agg
    ORDER BY Rank;
END
```

**(c) 其他建議的 SP**
- `sp_StartMatch` / `sp_EndMatch`：更新狀態 + 寫 Outbox
- `sp_IssueCard`：記紅黃牌
- `sp_GetMatchTimeline @MatchId`：撈該場所有事件

### 4.3 Outbox Publisher（背景服務）

`Match Service` 內一個 `BackgroundService`，每隔 N 秒：
1. `SELECT TOP 50 ... WHERE ProcessedUtc IS NULL ORDER BY OutboxId`
2. 用 MassTransit `IPublishEndpoint` 發布（依 `EventType` 反序列化 payload）
3. `UPDATE ... SET ProcessedUtc = SYSUTCDATETIME()`

> 這樣即使「DB commit 成功但發訊息前服務掛掉」，重啟後仍會補發 → **at-least-once**，下游需做冪等。

---

## 5. 其他服務的資料表

### SubscriptionDb
```sql
CREATE TABLE Subscriptions (
    SubscriptionId UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    UserId   UNIQUEIDENTIFIER NOT NULL,
    TeamId   UNIQUEIDENTIFIER NOT NULL,      -- 追蹤的球隊
    CreatedUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_User_Team UNIQUE (UserId, TeamId)
);
```
SP：`sp_AddSubscription`、`sp_RemoveSubscription`、`sp_GetUserSubscriptions`、`sp_GetSubscribersByTeam`（給 Notification Service 查誰在追這隊）。

### NotificationDb
```sql
CREATE TABLE Notifications (
    NotificationId UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    UserId   UNIQUEIDENTIFIER NOT NULL,
    Message  NVARCHAR(300) NOT NULL,
    MatchId  UNIQUEIDENTIFIER NULL,
    IsRead   BIT NOT NULL DEFAULT 0,
    CreatedUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
```

---

## 6. Subscriber 處理邏輯

### Live Score Service
- consume `GoalScored` / `MatchEnded` → 更新即時比分讀模型 → 透過 SignalR Hub `await hub.Clients.Group(matchId).SendAsync("ScoreUpdated", dto)`
- 前端只訂閱自己在看的比賽（SignalR Group = MatchId）

### Notification Service
- consume `GoalScored` → 呼叫 Subscription Service 查 `sp_GetSubscribersByTeam` → 對每個訂閱者寫一筆 Notification → SignalR 推給該 user
- **冪等**：用事件帶的唯一鍵（如 `MatchId + Minute + PlayerId`）或 MassTransit `MessageId` 去重

---

## 7. SignalR Hub 設計

```csharp
public class LiveScoreHub : Hub
{
    // 前端進入某場比賽頁時呼叫
    public Task JoinMatch(string matchId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"match-{matchId}");

    public Task LeaveMatch(string matchId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"match-{matchId}");
}
```
前端：`connection.on("ScoreUpdated", dto => updateUI(dto))`

---

## 8. 前端頁面

| 頁面 | 功能 | 資料來源 |
|------|------|---------|
| 賽事列表 | 所有比賽、狀態 | REST (Match) |
| 比賽詳情 | 即時比分、時間軸 | REST + SignalR group |
| 小組積分榜 | 排名 | REST `sp_GetGroupStandings` |
| 訂閱中心 | 勾選追蹤球隊 | REST (Subscription) |
| 通知鈴鐺 | 即時通知 toast | SignalR + REST |
| 管理頁（模擬器） | 手動送進球/紅黃牌 | REST (Match) |

> 「管理頁模擬器」很重要——它扮演賽事資料來源，按一下「進球」就能觸發整條 Pub/Sub 鏈路，方便 demo。

---

## 8.5 Ingestion Job 設計（第一個實作）

**型態**：.NET Worker Service（`BackgroundService`），可獨立容器化。

**兩種資料來源、不同節奏**：

| 類型 | 內容 | 頻率 | 策略 |
|------|------|------|------|
| **deadball** | 賽程、隊伍、球員名單、最終結果（相對靜態） | 低頻（每數分鐘 / 啟動時） | 全量或增量 upsert |
| **live** | 進行中比賽的即時事件、比分變動 | 高頻（每 5~15 秒輪詢） | 只處理「新增/變化」的事件 |

**核心流程**：
1. **Fetch**：HTTP 取得外部資料（`HttpClient` + Polly 重試）
2. **Parse / Map**：解析 → 對應到我們的 domain model（隊伍、比賽、事件）
3. **Dedup / Diff**：與已知狀態比對，找出「新事件」「比分變化」（避免重複送進球）
4. **Upsert**：透過 Match Service API（或直接 SP）寫入；live 進球 → 觸發 `sp_RecordGoal`

**待討論（下一步你會帶來）**：
- 外部網站 API endpoint 與回傳資料格式
- 我方對應的 DTO / API 契約（Ingestion → Match Service）
- 外部 ID 與我方 `Guid` 的對應（建議加 `ExternalId` 欄位做 mapping）
- 輪詢頻率、速率限制、是否需要 ETag / If-Modified-Since

**設計建議（先記著）**：
- 在 `Teams` / `Matches` 加 `ExternalId NVARCHAR` 欄位，做外部來源對應與冪等 upsert
- live 事件去重：以「外部事件 ID」或「比賽 + 分鐘 + 球員」當唯一鍵
- 擷取失敗要能重試且不漏資料 → 記錄上次擷取的游標 / 時間戳

---

## 9. docker-compose（服務清單）

```
- sqlserver        (mcr.microsoft.com/mssql/server:2022-latest)
- rabbitmq         (rabbitmq:3-management，含管理介面 :15672)
- match-service
- subscription-service
- livescore-service
- notification-service
- api-gateway
- frontend         (nginx 靜態 / vite preview)
```

---

## 10. 實作里程碑

| 里程碑 | 內容 | 驗收 |
|--------|------|------|
| **M1** | Match Service + MatchDb + SP（CRUD、記進球、upsert by ExternalId） | Swagger 能記錄進球、比分正確 |
| **M2** | **Ingestion Job**：從外部網站擷取 deadball + live，寫入 MatchDb | Job 跑起來後，DB 有真實賽程與即時比分 |
| **M3** | 接 RabbitMQ，Match 發 / Notification 收（先印 log）+ Outbox Pattern | RabbitMQ 管理介面看到訊息流動；殺 MQ 重啟仍補發 |
| **M4** | Live Score Service + SignalR + React 即時看板 | 真實 live 資料進來，前端比分即時跳動 |
| **M5** | Subscription + Notification 個人化 + docker-compose | 一鍵啟動全系統，訂閱者收到通知 |

> 依你的決定，**M2 的 Ingestion Job 是第一個動手實作的目標**；M1 的 Match Service 會與它一起最小化建起來（先有能 upsert 的 API/SP，Job 才有地方寫資料）。

---

## 11. 可延伸的加分項（履歷/面試）

- **冪等消費**：MassTransit `InMemoryOutbox` 或自建去重表
- **死信佇列 (DLQ)**：故意丟錯，展示重試與 DLQ
- **健康檢查**：`/health` + docker healthcheck
- **OpenTelemetry**：跨服務分散式追蹤（看一個進球事件如何流過各服務）
- **Saga / State Machine**：用 MassTransit Saga 管理「一場比賽的生命週期」
- **整合測試**：Testcontainers 啟動真實 SQL Server + RabbitMQ 跑測試
