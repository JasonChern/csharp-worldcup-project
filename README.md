# WorldCup Live Hub

世足主題的後端微服務練習專案。第一階段（M1 + M2）：從台灣運彩賽前(deadball) API 擷取 **2026 世界盃** 的賽程、玩法、賠率，寫入 MSSQL。

> 設計文件：`docs/ARCHITECTURE.md`、`docs/INGESTION-SPEC.md`、`docs/STORED-PROCEDURES.md`

## 技術
.NET 8、ASP.NET Core Minimal API、Dapper + **MSSQL Stored Procedure（TVP 批次 / set-based MERGE）**、Worker Service、docker-compose。

## 結構（Clean Architecture）

```
src/
  Contracts/                     共用傳輸 DTO（UpsertFixturesRequest…）
  MatchService/                  ← 完整分層，相依向內 Api→Infrastructure→Application→Domain
    Domain/                      實體（Match/Team/Market/Selection/OddsSnapshot）
    Application/                 ports（IFixtureRepository…）+ 用例（FixtureIngestionService）
    Infrastructure/              Dapper 實作、TVP 組裝、DbInitializer
    Api/                         Minimal API 端點 + DI
  IngestionJob/                  ← 輕量分層（Worker）
    Fetching/                    抓 en+zh JSON（來源 DTO + HttpClient）
    Mapping/                     合併 en+zh、過濾世足、換算賠率
    Publishing/                  POST 到 Match Service
  LiveIngestion/                 ← M-Live：高頻抓 Live feed，解析 ldss（比分/狀態）+ in-play 賠率
  OutboxPublisher/               ← M3：輪詢 OutboxMessages → MassTransit/RabbitMQ → 標記已發
  NotificationService/           ← M3+：訂閱 OddsChanged / MatchScoreChanged / MatchStatusChanged → 印 log
  RealtimeService/               ← M4：SignalR Hub + 訂閱事件 → push 比分/狀態給前端
  frontend/                      ← M4：React + Vite + TS 即時看板（nginx 提供）
db/
  01_schema.sql 02_types.sql 03_procedures.sql   啟動時由 DbInitializer 套用
```

## Pub/Sub（M3：Outbox Pattern + RabbitMQ）
- `sp_IngestWorldCupBatch` 在**同一交易**內寫 `OddsSnapshots` 與 `OutboxMessages`（賠率**真正變動**才寫事件，首次載入不算）。
- `OutboxPublisher` 輪詢未發送事件 → MassTransit 發布 `OddsChanged` 到 RabbitMQ → 標記 `ProcessedUtc`（at-least-once）。
- `NotificationService` 訂閱 `OddsChanged` 並印 log。

## 資料模型
`Teams → Matches → Markets → MarketSelections → OddsSnapshots(時序)`
- 前四層依各自 ExternalId 冪等 upsert；球隊以**英文名**為 key、`NameZh` 為中文。
- 賠率 `DecimalOdds = pu/pd + 1`；`OddsSnapshots` **只在賠率變動時**寫。
- 雙語：en 版為 canonical，zh 版依語言無關 id（`id`/`ms.id`/`cs.id`）對應填中文。
- **資料來源區分（前端用）**：`Markets.Source` = `'Pre'`(賽前/deadball) / `'Live'`(即時)；`Matches.Status`(Scheduled/Live/Ended) + `LivePhase`/`MatchMinute`。

## Live（M-Live：進行中）
- 來源 `Live/Games`：`ldss`(字串化 JSON) 給比分/狀態/分鐘，`ms` 給 in-play 賠率，用 `er` 對回賽事。
- 開賽即離開 Pre feed → 賽前盤看 Pre、in-play 盤看 Live（互補）。
- `sp_IngestLiveBatch` 同交易內更新比分/狀態 + in-play 賠率，比分/狀態/賠率變動才發事件（`MatchScoreChanged`/`MatchStatusChanged`/`OddsChanged`）。
- 比賽結束：feed 給 ended 或「從 feed 消失超過寬限」→ 標 Ended（對帳）。

## 啟動

```bash
docker compose up --build
```

啟動後：
- **前端即時看板：http://localhost:8088** ← 開這個看比分即時跳動
- Match Service：http://localhost:5080
- Realtime（SignalR）：http://localhost:5090（hub: `/hubs/live`）
- SQL Server：localhost:1433（sa / Your_strong_Pass123）
- RabbitMQ 管理介面：http://localhost:15672（guest / guest）
- Ingestion 每 5 分鐘擷取賽前；LiveIngestion 每 ~12 秒擷取進行中；比分/賠率變動 → Outbox → RabbitMQ → SignalR/Notification

## 即時看板（M4）
- `RealtimeService` 訂閱 `MatchScoreChanged`/`MatchStatusChanged`，經 SignalR Hub（`/hubs/live`）推給前端。
- 前端載入時打 `GET /api/matches` 取初始清單，再以 SignalR 接收 `ScoreUpdated`/`StatusUpdated` 即時更新。

## API

| 方法 | 路徑 | 說明 |
|------|------|------|
| GET  | `/health` | 健康檢查 |
| POST | `/api/fixtures/upsert` | 批次寫入賽事/玩法/賠率（Ingestion 用） |
| GET  | `/api/matches?tournament=29614` | 賽程列表（雙語、含比分/狀態） |
| GET  | `/api/match-markets?match={MatchExternalId}` | 某場玩法 + 各選項最新賠率 |

## 範圍邊界
- deadball **不含比分/進球/live 狀態**；本階段只建賽程+玩法+賠率。
- 即時比分（進球→事件→SignalR）為後續階段，需 live 端點。
