# Live Ingestion Spec — 台灣運彩 Live API

> 對應 `docs/INGESTION-SPEC.md`（賽前）。本文件規劃**進行中（in-play）**的擷取：比分、賽事狀態、in-play 賠率。

---

## 1. 資料來源

| 項目 | 值 |
|------|-----|
| Endpoint | `https://blob3rd.sportslottery.com.tw/apidata/Live/Games.en.json`（+ `.zh.json`） |
| 內容 | **只列目前進行中的場次** |
| 過濾 | 同樣 `ti == "29614"`（世足） |

每場 game 的欄位 = 賽前那套 + 多兩個 live 欄位：
- `lds`：實測為 `null`，忽略
- **`ldss`**：**被字串化的 JSON**，內含 live 狀態（要解析兩次）

`ldss` 解析後：
```json
{
  "id": "sr:match:66456998",     // == er，對回我方 MatchExternalId
  "matchTime": "1:15",            // 進行時間（分:秒）
  "status": "1h",                 // 狀態碼
  "scores": {
    "CURRENT_SCORE": { "home": 0, "away": 0 },   // ← 目前比分
    "PERIOD1_SCORE": { "home": 0, "away": 0 }
  },
  "tsMatchTime": "2026-06-21T16:02:03.699Z",
  "sourceEventId": "66456998"
}
```

`ms`（in-play 玩法）：結構與賽前相同（`cs[].pd/pu` 字串），**但**
- 市場集合不同（in-play 盤：Total / 1st Half / 角球 / 單雙…，**無全場不讓分**）
- 每個玩法 `iir = true`（in-running）
- market / selection 的 **id 是新的一組**（與賽前不衝突，會以新 Markets/Selections 進表）

---

## 2. 關鍵前提：Pre 與 Live 是互補的

- **一旦開賽，該場從 Pre feed 消失**（已驗證）。賽前賠率來自 Pre、in-play 賠率來自 Live。
- 同一場兩 feed 都用 `er`(sr:match) 串得起來。
- 賠率格式與換算 (`pu/pd + 1`) 完全相同 → **換算邏輯可重用**。

---

## 3. 欄位對應

| 來源 | → 我方 |
|------|--------|
| `ldss.id` / `er` | `Matches.ExternalId`（對回同一場） |
| `ldss.scores.CURRENT_SCORE.home/away` | `Matches.HomeScore / AwayScore` |
| `ldss.status`（raw） | `Matches.LivePhase`（新欄位，存 raw 如 "1h"） |
| 由 status 推導 | `Matches.Status`（Scheduled/Live/Ended） |
| `ldss.matchTime` | `Matches.MatchMinute`（新欄位，顯示用） |
| `ms[]` / `cs[]` | 同賽前 → Markets / MarketSelections / OddsSnapshots（`iir`→`Markets.IsInPlay`） |

**狀態碼對照（邊跑邊補；目前只見 `1h`）**
| raw status | DomainStatus |
|-----------|--------------|
| ns / not_started | Scheduled |
| 1h / ht / 2h / (其他進行中) | Live |
| ended / ft / aet / ap / pen | Ended |
| 未知但出現在 live feed | Live（預設） |

> 對照在 **App(Mapper)** 端做，SP 只存結果 + raw phase。

---

## 4. 事件（Outbox → RabbitMQ，沿用 M3 機制）

| 事件 | 觸發 |
|------|------|
| `MatchScoreChanged` | CURRENT_SCORE 與我方現存比分不同 |
| `MatchStatusChanged` | DomainStatus / LivePhase 改變（如 Scheduled→Live、1h→ht、→Ended） |
| `OddsChanged` | in-play 賠率變動（重用 M3 既有邏輯） |

> 進球細節（射手、紅黃牌）**此 feed 沒有**；`MatchScoreChanged` 帶 old→new 比分 + matchMinute，「進球」由比分差推斷（無射手名）。

---

## 5. 比賽結束的判定（重要眉角）

Live feed 只列進行中場次 → 某場結束時通常是**從 feed 消失**，不一定收得到 `ended`。策略：
1. **首選**：`ldss.status` 出現 ended/ft → 標記 Ended、發 `MatchStatusChanged`。
2. **後備（對帳）**：某場我方為 `Live`，但本輪 live feed 沒有它 → 視為已結束（可加「連續 N 輪缺席」門檻避免抖動），標記 Ended。

---

## 6. 資料模型異動（MatchDb）

```sql
ALTER TABLE dbo.Matches ADD LivePhase NVARCHAR(16) NULL;     -- raw status "1h"
ALTER TABLE dbo.Matches ADD MatchMinute NVARCHAR(8) NULL;    -- "1:15"
ALTER TABLE dbo.Markets ADD IsInPlay BIT NOT NULL DEFAULT 0; -- 來自 ms.iir
```
（`HomeScore/AwayScore/Status` 沿用既有欄位。）

新 TVP：
```sql
CREATE TYPE dbo.LiveStateTvp AS TABLE (
    MatchExternalId NVARCHAR(64) NOT NULL,
    HomeScore       INT NOT NULL,
    AwayScore       INT NOT NULL,
    DomainStatus    VARCHAR(20) NOT NULL,    -- Scheduled/Live/Ended
    LivePhase       NVARCHAR(16) NULL,
    MatchMinute     NVARCHAR(8) NULL
);
```

---

## 7. SP：`sp_IngestLiveBatch`

一個交易內完成（重用既有 upsert SP + 新增 live 狀態處理）：
1. `sp_UpsertTeams` / `sp_UpsertMatches`（live 場次若我方還沒有就建；teams/matches 來自 game 頂層）
2. `sp_UpsertMarkets` / `sp_UpsertSelections`（in-play 盤，`IsInPlay=1`）
3. **Live 狀態**：以 `#liveCalc` 比對每場現存 score/status 與本次
   - 比分不同 → `UPDATE Matches` + Outbox `MatchScoreChanged`
   - 狀態不同 → `UPDATE Matches` + Outbox `MatchStatusChanged`
4. **Odds**：同 M3 `#calc` 邏輯 → OddsSnapshots（變動才寫）+ Outbox `OddsChanged`
5. 回報：更新場次數、score/status/odds 變動事件數

對帳結束（選用）：`sp_EndStaleLiveMatches @ActiveExternalIds dbo.IdTvp` → 把 Live 但不在清單者標 Ended + 發事件。

---

## 8. 流程與節奏

```
LiveWorker（高頻，每 10~15 秒）
  1. GET Live/Games.en + .zh
  2. 過濾 ti==29614；解析 ldss
  3. Map → UpsertLiveRequest（fixtures+markets+odds 重用，外加 LiveState）
  4. POST /api/live/upsert → sp_IngestLiveBatch
  5.（選用）對帳：把消失的 Live 場次標 Ended
```
- 賽前 `Worker`（每 5 分鐘，吃 Pre）維持不變。
- 兩者並存：未開賽的場次有賽前盤，進行中的場次有 in-play 盤 + 比分/狀態。

---

## 9. 契約（Contracts）

```csharp
public record UpsertLiveRequest(
    IReadOnlyList<UpsertFixtureRequest> Fixtures,   // 重用：teams/matches/markets/odds
    IReadOnlyList<MatchLiveState> LiveStates);

public record MatchLiveState(
    string MatchExternalId, int HomeScore, int AwayScore,
    string DomainStatus, string? LivePhase, string? MatchMinute);

public record MatchScoreChanged(
    string MatchExternalId, int OldHome, int OldAway,
    int NewHome, int NewAway, string? MatchMinute, DateTime ChangedUtc);

public record MatchStatusChanged(
    string MatchExternalId, string OldStatus, string NewStatus,
    string? LivePhase, DateTime ChangedUtc);
```

---

## 10. 待確認決定
1. **進球事件**：只發 `MatchScoreChanged`（含比分差），還是另外再拆「每進一球一則」？
2. **結束判定**：只靠 status，還是加「消失對帳」？
3. **Worker 佈署**：在現有 IngestionJob 內加第二個 BackgroundService（LiveWorker），還是獨立 LiveIngestion 專案？
4. **in-play 賠率**：確認一起收（建議收，反正同一個 feed 就有）。
5. **NotificationService**：擴充訂閱 Match*Changed 並印 log（建議）。
