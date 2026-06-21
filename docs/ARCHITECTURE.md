# WorldCup Live Hub — 架構文件（實作現況）

> 世足賽事即時資訊平台。展示：**.NET 微服務 + Clean Architecture**、**MSSQL + Stored Procedure（TVP 批次 / set-based MERGE / 變動偵測）**、**Outbox Pattern + RabbitMQ(MassTransit) Pub/Sub**、**SignalR 即時推送**、**React 前端**。
>
> 本文件描述「**目前實際做出來**」的系統。資料來源是台灣運彩賽前(Pre)與即時(Live) API；詳見 `INGESTION-SPEC.md`、`LIVE-INGESTION-SPEC.md`、`STORED-PROCEDURES.md`。

---

## 1. 系統總覽

```
台灣運彩 Pre API ─┐                                          ┌─→ NotificationService ─ log
台灣運彩 Live API ─┤                                          │   (訂閱事件)
                  ▼                                          │
   IngestionJob(賽前/5min)        ┌───────────────────────┐  │   ┌──────────────────────────┐
   LiveIngestion(即時/12s) ──POST─►│   MatchService (API)  │  │   │   RealtimeService        │
                  │               │  Clean Architecture   │  ├──►│  SignalR Hub /hubs/live   │
                  │               │  Dapper + SP(TVP)     │  │   │  (訂閱事件→推前端)        │
                  │               └──────────┬────────────┘  │   └────────────▲─────────────┘
                  │                          ▼               │                │ WebSocket
                  │                  ┌─────────────────┐     │   ┌────────────┴─────────────┐
   MatchClock ────┼─────────────┐   │  MatchDb (MSSQL) │     │   │  React 前端 (nginx :8088) │
   (直接發, 不走 Outbox)        │   │  + OutboxMessages│     │   │  看板/明細/走勢圖          │
                  │             │   └────────┬────────┘     │   └──────────────────────────┘
                  │             │            │ poll          │
                  │             │   ┌────────▼─────────┐     │
                  │             └──►│  OutboxPublisher │─────┘  (發布 OddsChanged /
                  │                 │  (輪詢→發布→標記) │         MatchScoreChanged /
                  │                 └────────┬─────────┘         MatchStatusChanged)
                  │                          ▼
                  └──────────────────►  RabbitMQ (MassTransit)  ◄── MatchClock
```

### 服務清單（實際）

| 服務 | 專案 | 角色 |
|------|------|------|
| **Contracts** | `src/Contracts` | 共用傳輸 DTO + 事件契約（被各服務參考） |
| **Match Service** | `src/MatchService/{Domain,Application,Infrastructure,Api}` | 唯一寫 MatchDb 者；REST API；Dapper 呼叫 SP；啟動時 `DbInitializer` 套用 `db/*.sql` |
| **Ingestion Job** | `src/IngestionJob` | Worker：賽前(Pre) 每 5 分鐘擷取 → `POST /api/fixtures/upsert` |
| **Live Ingestion** | `src/LiveIngestion` | Worker：即時(Live) 每 ~12 秒擷取(比分/狀態/in-play 賠率) → `POST /api/live/upsert`；並每輪直接發 `MatchClock` 到 RabbitMQ |
| **Outbox Publisher** | `src/OutboxPublisher` | Worker：輪詢 `OutboxMessages` → MassTransit 發布 → 標記已發（at-least-once） |
| **Notification Service** | `src/NotificationService` | Worker：訂閱 Odds/Score/Status 事件 → 印 log（個人化通知的雛形） |
| **Realtime Service** | `src/RealtimeService` | ASP.NET Core：SignalR Hub + 訂閱事件 → 廣播給前端 |
| **Frontend** | `src/frontend` | React + Vite + TS，nginx 提供 |

> **單一資料庫**：目前只有 `MatchDb`（一個庫）。原始設計的「Database per Service / Subscription DB / Notification DB / API Gateway」**尚未實作**（見 §9 未實作項目）。OutboxPublisher 直接讀 MatchDb 的 Outbox（屬 Match 邊界內）。

---

## 2. 技術棧

- 後端：.NET 8、ASP.NET Core Minimal API、**Dapper + MSSQL Stored Procedure**
- 訊息：**RabbitMQ + MassTransit 8.2**；**Outbox Pattern**
- 即時：**SignalR**
- 前端：**React 18 + Vite + TypeScript + @microsoft/signalr + recharts**，nginx
- 容器：**docker-compose** 一鍵啟動（SQL Server 2022、RabbitMQ 3-management、各服務、前端）

---

## 3. 事件契約（`WorldCup.Contracts.Events`）

```csharp
// 賠率變動（只在「真正變動」時發；走 Outbox）
public record OddsChanged(string MatchExternalId, string MarketExternalId, string SelectionExternalId,
    string? Side, string SelectionNameEn, decimal OldOdds, decimal NewOdds, DateTime ChangedUtc);

// 比分變動（live；進球由比分差推斷，無射手；走 Outbox）
public record MatchScoreChanged(string MatchExternalId, int OldHome, int OldAway,
    int NewHome, int NewAway, string? MatchMinute, DateTime ChangedUtc);

// 狀態/階段變動（Scheduled→Live、1h→ht、→Ended；走 Outbox）
public record MatchStatusChanged(string MatchExternalId, string OldStatus, string NewStatus,
    string? LivePhase, DateTime ChangedUtc);

// 比賽時鐘（每 live 週期一則；LiveIngestion 直接發布、不走 Outbox）
public record MatchClock(string MatchExternalId, string? MatchMinute, string? LivePhase, bool Running);
```

> 為何 `MatchClock` 不走 Outbox：時鐘是高頻、可丟的；掉一則下一輪就校正，不需要持久化/重送。其餘三種事件需要與 DB 寫入一致，故走 Outbox。
>
> **MassTransit 提醒**：不同服務若用**同名 consumer 類別**會產生相同佇列名而互搶訊息（破壞 fan-out）。RealtimeService 的消費者刻意命名為 `ScoreBroadcastConsumer` 等，與 NotificationService 區隔。

---

## 4. 資料庫設計（MatchDb，單一庫）

腳本在 `db/`，啟動時由 `DbInitializer`（以 `GO` 分批、冪等）套用。

```sql
Teams(TeamId, NameEn UNIQUE, NameZh, GroupCode)
Matches(MatchId, ExternalId UNIQUE, SourceGameId, HomeTeamId, AwayTeamId,
        KickoffUtc, TournamentExternalId, TournamentNameEn, TournamentNameZh,
        Status, HomeScore, AwayScore, LivePhase, MatchMinute, LastSeenLiveUtc)
Markets(MarketId, ExternalId UNIQUE, MatchId, NameEn, NameZh, MarketTypeCode,
        Line, Source, UpdatedUtc)            -- Source = 'Pre' | 'Live'
MarketSelections(SelectionId, ExternalId UNIQUE, MarketId, NameEn, NameZh, Side)  -- Side H/D/A
OddsSnapshots(SnapshotId, SelectionId, Pd, Pu, DecimalOdds, FetchedUtc)           -- 時序
OutboxMessages(OutboxId, EventType, Payload, CreatedUtc, ProcessedUtc)
```

設計要點：
- **雙語**：球隊以 `NameEn` 為 canonical key、`NameZh` 為顯示；玩法/選項也雙語。擷取時抓 en+zh 兩版，靠語言無關 id 合併（見各 ingestion spec）。
- **賠率 = `pu/pd + 1`**；`OddsSnapshots` **只在賠率變動時**寫一筆（變動偵測）。
- **來源區分**：`Markets.Source`（`Pre`/`Live`）讓前端分「賽前盤/即時盤」。
- **Outbox**：與資料寫入**同一交易**寫入待發事件，確保一致。

SP 與 TVP 詳見 `STORED-PROCEDURES.md`。核心：
- `sp_IngestWorldCupBatch`（賽前）、`sp_IngestLiveBatch`（即時）：TVP 批次、單一交易、變動才寫快照/事件。
- 讀取：`sp_GetMatches`（含主盤 1x2 賠率）、`sp_GetMatchMarkets`（固定排序）、`sp_GetOddsHistory`（走勢）。
- Outbox：`sp_GetUnprocessedOutbox` / `sp_MarkOutboxProcessed`。

> 原始設計的 `Players` / `MatchEvents`（含射手、紅黃牌、`sp_RecordGoal`、小組積分 `sp_GetGroupStandings`）**未實作**——因為運彩 live feed 沒有射手/卡片資料，進球只能由比分差推斷。

---

## 5. REST API（Match Service）

| 方法 | 路徑 | 用途 |
|------|------|------|
| GET  | `/health` | 健康檢查 |
| POST | `/api/fixtures/upsert` | 賽前批次寫入（IngestionJob） |
| POST | `/api/live/upsert` | 即時批次寫入（LiveIngestion） |
| GET  | `/api/matches?tournament=29614` | 賽程列表（雙語、比分/狀態/開賽時間、主盤 1x2 賠率） |
| GET  | `/api/match-markets?match={er}` | 某場玩法 + 各選項最新賠率（含 Source；即時盤優先、主/和/客排序） |
| GET  | `/api/odds-history?selection={cs.id}` | 單一選項賠率時序（走勢圖） |

CORS 開放前端來源（`localhost:8088` / `5173`）。

---

## 6. 即時推送（SignalR）

`RealtimeService` 訂閱 RabbitMQ 事件，經 Hub `/hubs/live` 廣播給所有前端：

| 事件來源 | SignalR 訊息 | 內容 |
|----------|--------------|------|
| MatchScoreChanged | `ScoreUpdated` | 比分 + 分鐘 |
| MatchStatusChanged | `StatusUpdated` | 狀態 + 階段 |
| OddsChanged | `OddsUpdated` | 選項 + 新賠率 |
| MatchClock | `ClockUpdated` | 權威分鐘 + running |

> 目前是 `Clients.All` 廣播（公開資料）。要做「只推訂閱球隊」需加認證 + SignalR Group/`Clients.User` 在伺服器端過濾（見 §9）。

---

## 7. 前端（React 即時看板）

- 載入打 `/api/matches` 取初始清單，再以 SignalR 接收 `ScoreUpdated`/`StatusUpdated`/`OddsUpdated`/`ClockUpdated` 即時更新（不輪詢）。
- **看板**：每場顯示隊名、比分（未開賽顯示開賽時間）、即時狀態膠囊、**主盤 1x2 賠率**。比賽時間在前端**每秒平滑走鐘**（以伺服器權威分鐘為錨、收到 ClockUpdated 校準、超過 30s 無更新則凍結）。
- **點開明細**：玩法分「即時盤 / 賽前盤」兩區，固定排序（玩法類型、選項主/和/客）。
- **走勢圖**：點任一賠率 → 彈出 modal 顯示該選項賠率走勢（recharts），收到 OddsUpdated 即時長新點。

---

## 8. docker-compose（實際服務）

```
sqlserver(1433, 持久化 volume mssql-data)、rabbitmq(5672/15672)、
matchservice(5080)、ingestion、liveingestion、outboxpublisher、
notificationservice、realtimeservice(5090)、frontend(8088)
```
啟動：`docker compose up --build` → 前端 http://localhost:8088、RabbitMQ http://localhost:15672(guest/guest)。

---

## 9. 尚未實作 / 可延伸

- **使用者系統 + 訂閱 + 個人化通知**（Subscription Service、NotificationDb、SignalR group 過濾）
- **認證/授權**：Hub 與 ingestion 寫入端目前無 auth（公開資料、僅本機 demo）
- **API Gateway**（YARP）
- 小組積分榜、進球時間軸（需有射手/卡片來源，運彩 feed 沒有）
- 健康檢查擴充、**OpenTelemetry** 分散式追蹤、**DLQ**、**整合測試（Testcontainers）**

---

## 10. 里程碑（已完成）

| 里程碑 | 內容 | 狀態 |
|--------|------|------|
| M1+M2 | Match Service + MatchDb + SP(TVP) + Ingestion（賽前 deadball、雙語、賠率） | ✅ |
| M3 | Outbox Pattern + RabbitMQ（OddsChanged） | ✅ |
| M-Live | Live 擷取：比分/狀態 + in-play 賠率 + Pre/Live 來源、結束對帳 | ✅ |
| M4 | SignalR + React 即時看板 | ✅ |
| 加值 | 比賽時鐘走鐘、賠率推前端（主盤 + 明細）、賠率走勢圖、未開賽顯示開賽時間 | ✅ |
