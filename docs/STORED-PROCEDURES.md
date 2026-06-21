# Stored Procedures / TVP（實作現況）— MatchDb

> 程式碼真實來源：`db/01_schema.sql`（表）、`db/02_types.sql`（TVP）、`db/03_procedures.sql`（SP）。本文件是對照說明。
> 由 Match Service 的 `DbInitializer` 在啟動時以 `GO` 分批、冪等套用。

---

## 設計原則

- **TVP 批次 + set-based MERGE**：Ingestion 一次帶整批（賽事/玩法/選項/賠率），透過 table-valued parameter 進 SP，一次往返、一個交易。
- **解耦外部 ID**：子層 TVP 帶**父層的 ExternalId**，SP 內 JOIN 解析成內部鍵（Ingestion 不知道我方內部鍵）。
- **雙語**：球隊以 `NameEn` 為 key（+`NameZh`）；玩法/選項雙語。
- **賠率只在變動時寫**：`OddsSnapshots` 比對「該選項最新快照」，不同才 insert。
- **Outbox 同交易**：資料變更與待發事件在同一交易寫入。

---

## TVP（`db/02_types.sql`）

| Type | 欄位（重點） |
|------|------|
| `TeamTvp` | `NameEn`, `NameZh` |
| `MatchTvp` | `MatchExternalId`(er), `SourceGameId`, `HomeTeamNameEn`, `AwayTeamNameEn`, `KickoffUtc`, `TournamentExternalId`(ti), `TournamentNameEn`, `TournamentNameZh` |
| `MarketTvp` | `MarketExternalId`(ms.id), `MatchExternalId`(父), `NameEn`, `NameZh`, `MarketTypeCode`(ms.ti), `Line`(mv) |
| `SelectionTvp` | `SelectionExternalId`(cs.id), `MarketExternalId`(父), `NameEn`, `NameZh`, `Side`(H/D/A) |
| `OddsTvp` | `SelectionExternalId`, `Pd`, `Pu`, `DecimalOdds` |
| `LiveStateTvp` | `MatchExternalId`, `HomeScore`, `AwayScore`, `DomainStatus`(Scheduled/Live/Ended), `LivePhase`, `MatchMinute`(NVARCHAR(32)) |

> `LiveStateTvp.MatchMinute` 與 `Matches.MatchMinute` 為 `NVARCHAR(32)`，需容納傷停字串如 `"45:00 +4:55"`（早期 `NVARCHAR(8)` 會截斷報錯，已加寬；`02_types.sql` 內含「型別太小就 drop+重建」的自我遷移）。

---

## 寫入 SP

### 共用 upsert（冪等，set-based MERGE）
- `sp_UpsertTeams` — MERGE on `NameEn`，補 `NameZh`。
- `sp_UpsertMatches` — MERGE on `ExternalId`；JOIN Teams(NameEn) 解析主客隊；**只同步賽程（不碰比分/狀態）**。
- `sp_UpsertMarkets` — MERGE on `ExternalId`；JOIN Matches 解析 MatchId。
- `sp_UpsertSelections` — MERGE on `ExternalId`；JOIN Markets 解析 MarketId。

### `sp_IngestWorldCupBatch`（賽前 orchestrator）
單一交易：upsert teams/matches/markets/selections → 用 `#calc` 比對每個選項 prev vs new →
1. 快照：首次或變動才 insert `OddsSnapshots`；
2. Outbox：**只在 prev 已存在且不同**（真正變動）時寫 `OddsChanged`（首載不發）。
回傳：MatchesIn / MarketsIn / SelectionsIn / OddsSnapshotsInserted / OddsChangedEvents。

### `sp_IngestLiveBatch`（即時 orchestrator）
單一交易，重用上述 upsert，外加：
- in-play 玩法標 `Markets.Source = 'Live'`（賽前預設 `'Pre'`）。
- **Live 狀態**（`#live` 比對）：更新 `HomeScore/AwayScore/Status/LivePhase/MatchMinute/LastSeenLiveUtc`；比分變→Outbox `MatchScoreChanged`；狀態/階段變→`MatchStatusChanged`。
- **in-play 賠率**：同 `#calc` 邏輯，變動才寫快照 + `OddsChanged`。
- **結束對帳**：`Status='Live'` 且消失 ≥`@StaleGraceMinutes`(預設10) 且（**最後 `LivePhase='2h'`** 或 開賽 >125 分）→ 標 `Ended` + 發 `MatchStatusChanged`。（避免中場/空檔誤判結束。）
回傳：MatchesUpdated / ScoreChanges / StatusChanges / OddsSnapshotsInserted / OddsChangedEvents / MatchesEnded。

> `MatchClock` 事件**不在 SP**——由 `LiveIngestion` 每輪直接發到 RabbitMQ（不需持久化）。

---

## Outbox SP
- `sp_GetUnprocessedOutbox @Take` — 取未發送（`ProcessedUtc IS NULL`）。
- `sp_MarkOutboxProcessed @OutboxId` — 標記已發。

---

## 讀取 SP
- `sp_GetMatches @TournamentExternalId` — 賽程（雙語、比分、狀態、`LivePhase`、`MatchMinute`），並以 `OUTER APPLY` 帶出**主盤 1x2**（`Markets.NameEn='1x2'`）的主/和/客選項 id 與最新賠率。
- `sp_GetMatchMarkets @MatchExternalId` — 某場玩法+選項+最新賠率（含 `Source`）。**排序固定**：即時盤優先 → `MarketTypeCode` → 選項 `Side` 主(H)/和(D)/客(A)。
- `sp_GetOddsHistory @SelectionExternalId` — 單一選項 `OddsSnapshots` 時序（賠率走勢圖）。

---

## 呼叫（Match Service / Dapper）
把批次攤平成多個 `DataTable`（對應各 TVP），以 `AsTableValuedParameter("dbo.XxxTvp")` 一次呼叫 orchestrator（`SqlFixtureRepository`）。

> 賠率換算 `DecimalOdds = pu/pd + 1` 在 Ingestion(App) 端先算好再帶入。
