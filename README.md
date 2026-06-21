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
db/
  01_schema.sql 02_types.sql 03_procedures.sql   啟動時由 DbInitializer 套用
```

## 資料模型
`Teams → Matches → Markets → MarketSelections → OddsSnapshots(時序)`
- 前四層依各自 ExternalId 冪等 upsert；球隊以**英文名**為 key、`NameZh` 為中文。
- 賠率 `DecimalOdds = pu/pd + 1`；`OddsSnapshots` **只在賠率變動時**寫（`sp_RecordOddsIfChanged`）。
- 雙語：en 版為 canonical，zh 版依語言無關 id（`id`/`ms.id`/`cs.id`）對應填中文。

## 啟動

```bash
docker compose up --build
```

啟動後：
- Match Service：http://localhost:5080
- SQL Server：localhost:1433（sa / Your_strong_Pass123）
- Ingestion 每 5 分鐘擷取一次，將世足賽事寫入 DB

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
