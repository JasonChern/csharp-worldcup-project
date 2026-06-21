# Live Ingestion Spec（進行中 / in-play）— 實作現況

> `src/LiveIngestion`（獨立 Worker）：擷取台灣運彩**即時(Live)** API，更新比分/狀態 + in-play 賠率，並發出比賽時鐘。賽前見 `INGESTION-SPEC.md`。

---

## 1. 資料來源

| | 值 |
|---|---|
| Endpoint | `https://blob3rd.sportslottery.com.tw/apidata/Live/Games.{en,zh}.json` |
| 內容 | **只列進行中**的場次（賽前/已結束不在內） |
| 過濾 | `ti == "29614"` |
| 頻率 | 每 ~12 秒（`Live:IntervalSeconds`） |

比 Pre 多一個 **`ldss`** 欄位（**被字串化的 JSON**，需二次解析）：
```json
{ "id":"sr:match:...", "matchTime":"60:31", "status":"2h",
  "scores":{"CURRENT_SCORE":{"home":4,"away":0}} }
```
- `matchTime`：絕對比賽時鐘（1h 0→45、2h 45→90、傷停 `"45:00 +4:55"`）。
- `status`：`1h`/`paused`(中場)/`2h`/…；映射為 DomainStatus `Scheduled`/`Live`/`Ended`（在 feed 中且非 ended → Live）。
- `id` == `er` → 對回我方賽事。`ms[]` 為 in-play 盤（與賽前同結構，新一組 id）。

## 2. 欄位 → 我方

| 來源 | → |
|------|---|
| `ldss.scores.CURRENT_SCORE.home/away` | `Matches.HomeScore/AwayScore` |
| `ldss.status` | `Matches.LivePhase`（raw）→ 映射 `Status` |
| `ldss.matchTime` | `Matches.MatchMinute`（`NVARCHAR(32)`，含傷停） |
| `ms[]`/`cs[]` | Markets/Selections/Odds，**`Source='Live'`**，`DecimalOdds=pu/pd+1` |

## 3. 上傳契約 + SP

```csharp
UpsertLiveRequest(IReadOnlyList<UpsertFixtureRequest> Fixtures,    // 重用：teams/matches/markets/odds
                  IReadOnlyList<MatchLiveState> LiveStates)
MatchLiveState(MatchExternalId, HomeScore, AwayScore, DomainStatus, LivePhase, MatchMinute)
```
`POST /api/live/upsert` → `sp_IngestLiveBatch`（單一交易）：upsert 定義 + 標 `Source='Live'` + 更新 live 狀態 + in-play 賠率 + 結束對帳。詳見 `STORED-PROCEDURES.md`。

## 4. 事件（走 Outbox → RabbitMQ）

| 事件 | 觸發 |
|------|------|
| `MatchScoreChanged` | 比分與現存不同（進球由**比分差推斷**，無射手/卡片——feed 沒有） |
| `MatchStatusChanged` | DomainStatus / LivePhase 改變（含 →Ended） |
| `OddsChanged` | in-play 賠率變動（重用變動偵測） |

> 原規劃的逐球 `GoalInferred` **未做**（只發 MatchScoreChanged）；射手/紅黃牌此來源無法提供。

## 5. 比賽結束判定

Live feed 只列進行中 → 結束多半是「**從 feed 消失**」而非收到 ended。對帳（`sp_IngestLiveBatch` 內）：
標 `Ended` 條件 = `Status='Live'` 且 消失 ≥10 分（`@StaleGraceMinutes`）且（**最後 `LivePhase='2h'`**〔下半場踢完才會離開 feed〕**或** 開賽 >125 分鐘〔保險〕）。

> 為何這樣：中場(`paused`/`1h`)或短暫空檔**不**標結束（曾用「消失 2 分鐘就結束」→ 還在踢被誤判，已修正）；比賽回到 feed 會自動轉回 Live。

## 6. 比賽時鐘（走鐘）

- `LiveIngestion` 每輪對每場進行中賽事**直接發** `MatchClock(MatchExternalId, MatchMinute, LivePhase, Running)` 到 RabbitMQ（**不走 Outbox**：時鐘可丟、掉一則下輪校正）。`Running` = `Status=Live 且 非暫停階段`。
- `RealtimeService` → SignalR `ClockUpdated`。
- 前端 `clock.ts`：以權威分鐘為錨**每秒平滑外推**，收到 `ClockUpdated` 校準但**忽略小幅往回**（來源時間粗略/階梯式），**超過 30 秒無更新則凍結**（避免離開 feed 時暴衝）。

## 7. 流程

```
每 ~12 秒：
1. GET Live en + zh → 過濾 ti==29614 → 解析 ldss
2. 對每場發 MatchClock 到 RabbitMQ
3. Map → UpsertLiveRequest → POST /api/live/upsert（含結束對帳）
   （即使 0 場進行中也 POST，讓對帳能標結束）
```
