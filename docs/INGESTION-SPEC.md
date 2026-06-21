# Ingestion Spec — 台灣運彩 Deadball API

> 第一個實作目標（M2 + M1 最小化）：從台灣運彩賽前 API 擷取 **2026 世界盃** 的**賽程 + 玩法 + 賠率**，正規化後寫入 Match Service。
>
> **範圍決定（已定案）**：
> - 只限世足（`ti == "29614"`）
> - 賽事不存在就建檔（球隊、主客隊、開賽時間）
> - 玩法（markets）**全部都存**
> - 賠率**保留歷史快照**，且**只在賠率變動時**才寫新快照

---

## 1. 資料來源

| 項目 | 值 |
|------|-----|
| Endpoint | `https://blob3rd.sportslottery.com.tw/apidata/Pre/34740.1-Games.zh.json` |
| 類型 | **Pre（賽前 / deadball）** — 賽程 + 固定賠率 |
| 回傳 | JSON 陣列，每筆是一場 game（含多個投注市場 `ms`） |
| 編碼 | UTF-8（繁中隊名） |

**關於 `34740.1`**：這是**販售場次群組／盤別批次 ID**（對應每場的 `si` 欄位，全檔一致，也是檔名前綴），**不是**運動代號。結尾 `.1` 是版本序號。

**足球判別欄位**：`san == "FBL"`（Football）／`sn == "足球"` 才是運動別。此批次跨多聯賽（世足、瑞典甲、冰島超、巴西乙、白俄超…）但全是足球。

> ⚠️ **重要**：此 API **不含比分、進球事件、live 狀態**。它只能建立「賽程 + 賠率」。即時比分／進球需另一支 live 端點（下一步討論）。

---

## 2. 世界盃過濾條件

擷取後只保留世界盃賽事，用 game **頂層**的 `ti`：

```
game.ti == "29614"     // 2026世界盃 的 tournament id（最穩定）
```

> - `ti == "29614"` 對應 `tn == "2026世界盃"`、`cn == "國際"`。
> - **勿混淆**：`ms[]` 市場物件裡也有一個 `ti`（市場類型代號，如 "1"/"14"/"21"），那是不同層級的欄位。過濾用的是 **game 頂層** 的 `ti`。
> - 不建議用 `tn` 字串過濾（年份/版本可能變動）。

---

## 3. 來源欄位字典（只列我們會用到的）

### Game（頂層）

| 欄位 | 範例 | 意義 | 用途 |
|------|------|------|------|
| `id` | `"3474285.1"` | 運彩 game id | 次要 external id |
| `er` | `"sr:match:66456998"` | **Sportradar 賽事 id** | **主要 ExternalId** |
| `hn` | `"西班牙"` | 主隊名 | HomeTeam |
| `an` | `"沙烏地阿拉伯"` | 客隊名 | AwayTeam |
| `kt` | `"2026-06-22T00:00:00+08:00"` | 開賽時間（含時區） | KickoffUtc |
| `ti` | `"29614"` | 賽事（聯賽）id | 過濾鍵 + TournamentExternalId |
| `tn` | `"2026世界盃"` | 賽事名 | 顯示 |
| `san`| `"FBL"` | 運動別（足球） | 防呆 |
| `cn` | `"國際"` | 賽事所屬國別名 | （世足為「國際」，非球隊國別） |
| `s`  | `"True"` | 是否可投注 | 參考 |
| `ms` | `[...]` | 投注市場陣列 | 選用（賠率，見 §6） |

> **主客判定**：`bn` 格式為「客 @ 主」；`ms[].cs[].v` 的 `"H"` 對應 `hn`、`"A"` 對應 `an`、`"D"` 為和局。已驗證。
>
> **球隊無外部 ID**：此 feed 沒有球隊穩定 id，只有隊名。世足球隊＝國家，隊名相對穩定，先用「正規化隊名」當 team 的自然鍵。

### Market `ms[]`（選用 — 賠率功能才需要）

| 欄位 | 意義 |
|------|------|
| `id` | 市場 id |
| `name` | 市場名（如「不讓分」「讓分 0:1」「[總分]大小 2.5」） |
| `mv` | 盤口線值（讓分/大小的數字） |
| `cs` | 選項陣列 |

### Choice `cs[]`

| 欄位 | 意義 |
|------|------|
| `id` | 選項 id |
| `name` | 選項名 |
| `v` | `"H"`/`"A"`/`"D"` = 主/客/和 |
| `pd`, `pu` | 賠率分母/分子（見 §6 解碼） |

---

## 4. DTO 設計

### 4.1 來源原始 DTO（反序列化用，欄位名對齊 JSON）

放在 Ingestion Job 專案內部，僅用於 parse。

```csharp
// 對齊運彩 JSON，用 JsonPropertyName 映射短欄位
public class SlGameDto
{
    [JsonPropertyName("id")]  public string Id { get; set; } = "";
    [JsonPropertyName("er")]  public string? ExternalRef { get; set; }   // sr:match:xxxx
    [JsonPropertyName("hn")]  public string HomeName { get; set; } = ""; // 主
    [JsonPropertyName("an")]  public string AwayName { get; set; } = ""; // 客
    [JsonPropertyName("kt")]  public DateTimeOffset Kickoff { get; set; }
    [JsonPropertyName("ti")]  public string TournamentId { get; set; } = "";
    [JsonPropertyName("tn")]  public string TournamentName { get; set; } = "";
    [JsonPropertyName("san")] public string Sport { get; set; } = "";
    [JsonPropertyName("cn")]  public string CountryName { get; set; } = "";
    [JsonPropertyName("s")]   public string Sellable { get; set; } = "";
    [JsonPropertyName("ms")]  public List<SlMarketDto> Markets { get; set; } = new();
}

public class SlMarketDto
{
    [JsonPropertyName("id")]   public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("mv")]   public decimal? Line { get; set; }
    [JsonPropertyName("cs")]   public List<SlChoiceDto> Choices { get; set; } = new();
}

public class SlChoiceDto
{
    [JsonPropertyName("id")]   public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("v")]    public string? Side { get; set; }  // H/A/D
    [JsonPropertyName("pd")]   public decimal Pd { get; set; }
    [JsonPropertyName("pu")]   public decimal Pu { get; set; }
}
```

### 4.2 Ingestion → Match Service API 契約（正規化後）

這是 Job 呼叫 Match Service 的請求 body（與運彩格式解耦）。一場比賽連同它的玩法、選項、當下賠率一起送。

```csharp
public record UpsertFixtureRequest(
    string  MatchExternalId,        // = er，例 "sr:match:66456998"
    string  SourceGameId,           // = id，例 "3474285.1"（稽核用）
    string  HomeTeamName,           // = hn
    string  AwayTeamName,           // = an
    DateTimeOffset KickoffUtc,      // = kt（存 UTC）
    string  TournamentExternalId,   // = ti（"29614"）
    string  TournamentName,         // = tn
    IReadOnlyList<UpsertMarketRequest> Markets
);

public record UpsertMarketRequest(
    string  MarketExternalId,       // = ms.id
    string  Name,                   // = ms.name（如「不讓分」「[總分]大小 2.5」）
    string? MarketTypeCode,         // = ms.ti（市場類型代號）
    decimal? Line,                  // = ms.mv（盤口線值；1X2 類為 null）
    IReadOnlyList<UpsertSelectionRequest> Selections
);

public record UpsertSelectionRequest(
    string  SelectionExternalId,    // = cs.id
    string  Name,                   // = cs.name
    string? Side,                   // = cs.v（H/A/D；其他玩法可能為 null）
    decimal Pd,                     // = cs.pd
    decimal Pu,                     // = cs.pu
    decimal DecimalOdds             // = pu/pd + 1（算好送）
);

// 批次上傳（一次帶整批世足賽事）
public record UpsertFixturesRequest(IReadOnlyList<UpsertFixtureRequest> Fixtures);
```

> Match Service 端點：`POST /api/fixtures/upsert`。內部流程：
> - `sp_UpsertTeamByName`（依正規化隊名）
> - `sp_UpsertMatch`（依 `MatchExternalId`）
> - `sp_UpsertMarket`（依 `MarketExternalId`）
> - `sp_UpsertSelection`（依 `SelectionExternalId`）
> - `sp_RecordOddsIfChanged`（比對最新快照，**有變動才** insert 一筆 `OddsSnapshots`）

---

## 4.3 資料模型（MatchDb 新增表）

```
Teams                      球隊（依正規化隊名）
Matches                    一場比賽（ExternalId=er、HomeTeamId、AwayTeamId、KickoffUtc）
  └─ Markets               玩法定義（ExternalId=ms.id、MatchId、Name、MarketTypeCode、Line）
       └─ MarketSelections 選項定義（ExternalId=cs.id、MarketId、Name、Side）
            └─ OddsSnapshots  賠率時序快照（SelectionId、Pd、Pu、DecimalOdds、FetchedUtc）
```

- **前四層是「定義」**：依各自 ExternalId 做 idempotent upsert（不重複建、名稱/線值有變則更新）。
- **`OddsSnapshots` 是時序事實表**：`sp_RecordOddsIfChanged` 只在「該選項最新快照的 `DecimalOdds`（或 `Pd/Pu`）與本次不同」時才 insert，避免無變化的重複列。
- 賠率有變動時，可在同一交易內寫 Outbox `OddsChanged` 事件 → 前端即時更新。

```sql
CREATE TABLE Markets (
    MarketId    BIGINT IDENTITY PRIMARY KEY,
    ExternalId  NVARCHAR(64) NOT NULL UNIQUE,     -- ms.id
    MatchId     UNIQUEIDENTIFIER NOT NULL REFERENCES Matches(MatchId),
    Name        NVARCHAR(100) NOT NULL,           -- ms.name
    MarketTypeCode NVARCHAR(16) NULL,             -- ms.ti
    Line        DECIMAL(6,2) NULL,                -- ms.mv
    UpdatedUtc  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE MarketSelections (
    SelectionId BIGINT IDENTITY PRIMARY KEY,
    ExternalId  NVARCHAR(64) NOT NULL UNIQUE,     -- cs.id
    MarketId    BIGINT NOT NULL REFERENCES Markets(MarketId),
    Name        NVARCHAR(100) NOT NULL,           -- cs.name
    Side        CHAR(1) NULL                      -- cs.v: H/A/D
);

CREATE TABLE OddsSnapshots (
    SnapshotId  BIGINT IDENTITY PRIMARY KEY,
    SelectionId BIGINT NOT NULL REFERENCES MarketSelections(SelectionId),
    Pd          DECIMAL(10,2) NOT NULL,
    Pu          DECIMAL(10,2) NOT NULL,
    DecimalOdds DECIMAL(10,4) NOT NULL,           -- pu/pd + 1
    FetchedUtc  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Odds_Selection_Time ON OddsSnapshots(SelectionId, SnapshotId DESC);
```

> `Teams` / `Matches` 沿用 `docs/ARCHITECTURE.md` §4.1（含 `ExternalId`）。

---

## 5. 正規化與冪等規則

1. **時間**：`kt` 已含 `+08:00`，轉存 `KickoffUtc`（`DateTimeOffset.ToUniversalTime()`）。
2. **球隊 upsert**：以正規化隊名為鍵
   - trim、全形空白正規化；建議再維護一張別名表（運彩繁中名 → 標準隊名），避免長名不一致。
3. **比賽 upsert**：以 `MatchExternalId`(`er`) 為唯一鍵；若 `er` 為 null，fallback 用 `SourceGameId`(`id`)。
4. **只新增/更新賽程欄位**：deadball 不帶比分，**不可**覆寫 Match Service 已有的 `HomeScore/AwayScore/Status`（那些由 live 來源更新）。
5. **deadball 是低頻**：建議每 5~10 分鐘或啟動時跑一次。

---

## 6. 賠率解碼（核心）

歐洲十進位賠率：

```
decimalOdds = pu / pd + 1            // 等價於 (pd + pu) / pd
```

驗證：西班牙 `pd=20, pu=1` → `1/20+1 = 1.05`（大熱）；沙烏地 `pd=1, pu=9` → `10.0`；和局 `pd=4, pu=15` → `4.75`。

- 全部玩法（`ms[]` 的每個 `cs[]`）都換算並存。
- 寫入時算好 `DecimalOdds`，原始 `pd/pu` 也一併保留（稽核/重算）。

---

## 7. Ingestion Job 流程（M2 具體化）

```
1. HttpClient GET 34740.1-Games.zh.json（Polly 重試 + 逾時）
2. 反序列化為 List<SlGameDto>
3. Where(g => g.TournamentId == "29614")          // 只要世足（game 頂層 ti）
4. Map → List<UpsertFixtureRequest>（含 markets / selections / 算好的 DecimalOdds）
5. POST /api/fixtures/upsert（批次）
   - Match Service 端：upsert 定義 + sp_RecordOddsIfChanged（只在變動時寫快照）
6. log：擷取 N 場、新增/更新賽事數、新增賠率快照數
```

背景排程：`PeriodicTimer`（每 5 分鐘）或 `BackgroundService` 迴圈。

---

## 8. 待補充的 live 來源

deadball 已能建立賽程。要驅動整條 Pub/Sub（進球→事件→SignalR），還需要 **live 端點**，請下次提供：
- live API 的 URL（推測類似 `.../apidata/Live/...` 或 inplay 端點）
- 它是否帶：即時比分、進球分鐘、紅黃牌、比賽狀態（進行中/中場/結束）
- 是否有與 `er`(sr:match) 對應的欄位，方便接回同一場比賽

拿到後就能定義 live 的 DTO 與 `GoalScored / MatchStatusChanged` 的對應，串起 M3/M4。
