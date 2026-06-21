# Ingestion Spec（賽前 / deadball）— 實作現況

> `src/IngestionJob`（Worker）：擷取台灣運彩**賽前(Pre)** API 的 2026 世界盃**賽程 + 玩法 + 賠率**，正規化後 `POST /api/fixtures/upsert` 給 Match Service。即時(Live)見 `LIVE-INGESTION-SPEC.md`。

---

## 1. 資料來源

| | 值 |
|---|---|
| Endpoint | `https://blob3rd.sportslottery.com.tw/apidata/Pre/34740.1-Games.{en,zh}.json` |
| 內容 | 賽前賽程 + 固定賠率（**不含**比分/進行狀態） |
| 過濾 | game 頂層 `ti == "29614"`（2026 世界盃） |
| 頻率 | 每 5 分鐘（`Ingestion:IntervalSeconds`） |

- `34740.1` 是販售批次群組 id（檔名/`si`），**不是**運動代號；足球是 `san="FBL"`。
- `er` = Sportradar 賽事 id（`sr:match:...`），作主要 `MatchExternalId`；`id` 為運彩 game id（次要/稽核）。
- 主客：`hn`=主、`an`=客；`cs[].v` 的 `H`/`A`/`D` 對應 主/客/和。

## 2. 雙語合併（en + zh）

抓 **en 與 zh 兩版**，靠**語言無關 id** 對應後合併：
- 比賽以 `id` 對應；玩法以 `ms.id`、選項以 `cs.id`（都語言無關）。
- **英文為 canonical**（`NameEn`），中文填 `NameZh`。
- 球隊無外部 id，以同一場的主對主、客對客取得中英名，最後以**英文隊名**為 key。

## 3. 來源欄位 → 我方

| 來源 | → |
|------|---|
| `er`(fallback `id`) | `MatchExternalId` |
| `hn`/`an`（en+zh） | 主/客隊 `NameEn`/`NameZh` |
| `kt`（含 +08:00） | `KickoffUtc`（存 UTC） |
| `ti`/`tn` | `TournamentExternalId` / `TournamentNameEn`/`Zh` |
| `ms[]`：`id`/`name`/`ti`/`mv` | Markets：`ExternalId`/`NameEn,Zh`/`MarketTypeCode`/`Line`（**Source='Pre'**） |
| `cs[]`：`id`/`name`/`v`/`pd`/`pu` | MarketSelections + 賠率；`DecimalOdds = pu/pd + 1` |

> `pd`/`pu` 在來源是**字串**，於 Mapper 轉 decimal。賠率驗證：西班牙 `pd20/pu1` → `1.05`。

## 4. 上傳契約（Contracts）

```csharp
UpsertFixturesRequest(IReadOnlyList<UpsertFixtureRequest> Fixtures)
UpsertFixtureRequest(MatchExternalId, SourceGameId, HomeTeamNameEn, HomeTeamNameZh,
    AwayTeamNameEn, AwayTeamNameZh, KickoffUtc, TournamentExternalId,
    TournamentNameEn, TournamentNameZh, IReadOnlyList<UpsertMarketRequest> Markets)
UpsertMarketRequest(MarketExternalId, NameEn, NameZh, MarketTypeCode, Line, Selections)
UpsertSelectionRequest(SelectionExternalId, NameEn, NameZh, Side, Pd, Pu, DecimalOdds)
```
Match Service 端：`POST /api/fixtures/upsert` → `sp_IngestWorldCupBatch`（TVP 批次，見 `STORED-PROCEDURES.md`）。

## 5. 流程

```
1. GET Pre en + zh（HttpClient + Polly 重試）
2. 反序列化 → 過濾 ti==29614
3. 合併 en/zh、換算賠率 → UpsertFixturesRequest
4. POST /api/fixtures/upsert
5. log：賽事 / 玩法 / 選項 / 新增賠率快照 / 變動事件 數
```

## 6. 重要邊界
- **開賽即離開 Pre feed**：賽前盤只在未開賽時更新；開賽後該場的 in-play 盤改由 Live feed 提供（見 live spec）。兩者用同一 `er` 串接，賠率格式相同（`pu/pd+1`），以 `Markets.Source`（Pre/Live）區分。
- 冪等：四層定義表依各自 ExternalId upsert；賠率只在變動時寫快照。
