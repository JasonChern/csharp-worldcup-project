# Stored Procedures 規格（TVP 批次）— Match Service / MatchDb

> 對應 `docs/INGESTION-SPEC.md`。Ingestion 一次帶整批世足賽事（含玩法/選項/賠率），透過 **TVP（table-valued parameter）** 進 SP，全部 **set-based MERGE**，不逐筆往返。

---

## 0. 設計原則

- **解耦外部 ID**：Ingestion 只知道各層的 ExternalId（`er`/`ms.id`/`cs.id`），不知道我方內部鍵。所以子層 TVP 一律帶**父層的 ExternalId**，SP 內用 JOIN 解析成內部鍵。
- **冪等**：四層定義表用 `MERGE ... ON ExternalId`（球隊用正規化隊名）。重跑不重複建。
- **賠率只在變動時寫**：`OddsSnapshots` 用「比對最新快照」的 set-based insert。
- **一個交易**：orchestrator `sp_IngestWorldCupBatch` 依序呼叫各 SP，包在單一交易。
- **隊名正規化**：由 App 端先做（trim / 全形空白），SP 以 `Name` 比對；別名表為日後擴充。

---

## 1. User-Defined Table Types（TVP）

```sql
-- 球隊（去重後的隊名清單）
CREATE TYPE dbo.TeamTvp AS TABLE (
    Name        NVARCHAR(100) NOT NULL
);

-- 比賽
CREATE TYPE dbo.MatchTvp AS TABLE (
    MatchExternalId       NVARCHAR(64)  NOT NULL,   -- er
    SourceGameId          NVARCHAR(64)  NULL,        -- id（稽核）
    HomeTeamName          NVARCHAR(100) NOT NULL,    -- hn
    AwayTeamName          NVARCHAR(100) NOT NULL,    -- an
    KickoffUtc            DATETIME2     NOT NULL,     -- kt → UTC
    TournamentExternalId  NVARCHAR(32)  NOT NULL,    -- ti
    TournamentName        NVARCHAR(100) NULL         -- tn
);

-- 玩法
CREATE TYPE dbo.MarketTvp AS TABLE (
    MarketExternalId  NVARCHAR(64)  NOT NULL,        -- ms.id
    MatchExternalId   NVARCHAR(64)  NOT NULL,        -- 父層：er
    Name              NVARCHAR(100) NOT NULL,        -- ms.name
    MarketTypeCode    NVARCHAR(16)  NULL,            -- ms.ti
    Line              DECIMAL(6,2)  NULL             -- ms.mv
);

-- 選項
CREATE TYPE dbo.SelectionTvp AS TABLE (
    SelectionExternalId  NVARCHAR(64)  NOT NULL,     -- cs.id
    MarketExternalId     NVARCHAR(64)  NOT NULL,     -- 父層：ms.id
    Name                 NVARCHAR(100) NOT NULL,     -- cs.name
    Side                 CHAR(1)       NULL          -- cs.v: H/A/D
);

-- 賠率（當下值）
CREATE TYPE dbo.OddsTvp AS TABLE (
    SelectionExternalId  NVARCHAR(64)  NOT NULL,     -- cs.id
    Pd                   DECIMAL(10,2) NOT NULL,
    Pu                   DECIMAL(10,2) NOT NULL,
    DecimalOdds          DECIMAL(10,4) NOT NULL       -- pu/pd + 1
);
```

---

## 2. SP 簽章與核心邏輯

### 2.1 `sp_UpsertTeams`

```sql
CREATE OR ALTER PROCEDURE dbo.sp_UpsertTeams
    @Teams dbo.TeamTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.Teams AS t
    USING (SELECT DISTINCT Name FROM @Teams) AS src
        ON t.Name = src.Name
    WHEN NOT MATCHED THEN
        INSERT (Name, GroupCode) VALUES (src.Name, '?');  -- 世足小組待 live/編排補
END
```

### 2.2 `sp_UpsertMatches`

```sql
CREATE OR ALTER PROCEDURE dbo.sp_UpsertMatches
    @Matches dbo.MatchTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.Matches AS m
    USING (
        SELECT x.MatchExternalId, x.SourceGameId, x.KickoffUtc,
               x.TournamentExternalId, x.TournamentName,
               h.TeamId AS HomeTeamId, a.TeamId AS AwayTeamId
        FROM @Matches x
        JOIN dbo.Teams h ON h.Name = x.HomeTeamName
        JOIN dbo.Teams a ON a.Name = x.AwayTeamName
    ) AS src
        ON m.ExternalId = src.MatchExternalId
    WHEN MATCHED THEN
        UPDATE SET m.KickoffUtc = src.KickoffUtc        -- 只同步賽程，不碰比分/狀態
    WHEN NOT MATCHED THEN
        INSERT (ExternalId, SourceGameId, HomeTeamId, AwayTeamId,
                KickoffUtc, TournamentExternalId, TournamentName, Status)
        VALUES (src.MatchExternalId, src.SourceGameId, src.HomeTeamId, src.AwayTeamId,
                src.KickoffUtc, src.TournamentExternalId, src.TournamentName, 'Scheduled');
END
```

> 注意：`UPDATE` 子句**刻意不改** `HomeScore/AwayScore/Status`——那些由 live 來源負責（deadball 不可覆寫）。

### 2.3 `sp_UpsertMarkets`

```sql
CREATE OR ALTER PROCEDURE dbo.sp_UpsertMarkets
    @Markets dbo.MarketTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.Markets AS k
    USING (
        SELECT x.MarketExternalId, x.Name, x.MarketTypeCode, x.Line, m.MatchId
        FROM @Markets x
        JOIN dbo.Matches m ON m.ExternalId = x.MatchExternalId
    ) AS src
        ON k.ExternalId = src.MarketExternalId
    WHEN MATCHED THEN
        UPDATE SET k.Name = src.Name, k.MarketTypeCode = src.MarketTypeCode,
                   k.Line = src.Line, k.UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (ExternalId, MatchId, Name, MarketTypeCode, Line)
        VALUES (src.MarketExternalId, src.MatchId, src.Name, src.MarketTypeCode, src.Line);
END
```

### 2.4 `sp_UpsertSelections`

```sql
CREATE OR ALTER PROCEDURE dbo.sp_UpsertSelections
    @Selections dbo.SelectionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.MarketSelections AS s
    USING (
        SELECT x.SelectionExternalId, x.Name, x.Side, k.MarketId
        FROM @Selections x
        JOIN dbo.Markets k ON k.ExternalId = x.MarketExternalId
    ) AS src
        ON s.ExternalId = src.SelectionExternalId
    WHEN MATCHED THEN
        UPDATE SET s.Name = src.Name, s.Side = src.Side
    WHEN NOT MATCHED THEN
        INSERT (ExternalId, MarketId, Name, Side)
        VALUES (src.SelectionExternalId, src.MarketId, src.Name, src.Side);
END
```

### 2.5 `sp_RecordOddsIfChanged`（核心：只在變動時寫）

```sql
CREATE OR ALTER PROCEDURE dbo.sp_RecordOddsIfChanged
    @Odds dbo.OddsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.OddsSnapshots (SelectionId, Pd, Pu, DecimalOdds)
    SELECT s.SelectionId, o.Pd, o.Pu, o.DecimalOdds
    FROM @Odds o
    JOIN dbo.MarketSelections s ON s.ExternalId = o.SelectionExternalId
    OUTER APPLY (
        SELECT TOP 1 last.DecimalOdds
        FROM dbo.OddsSnapshots last
        WHERE last.SelectionId = s.SelectionId
        ORDER BY last.SnapshotId DESC
    ) prev
    WHERE prev.DecimalOdds IS NULL          -- 第一次
       OR prev.DecimalOdds <> o.DecimalOdds; -- 有變動

    SELECT @@ROWCOUNT AS SnapshotsInserted;  -- 回報這次寫了幾筆
END
```

> M3 接 MQ 時，在此 INSERT 後於同交易把變動的 selection 寫進 `OutboxMessages`（`OddsChanged`）。

### 2.6 `sp_IngestWorldCupBatch`（orchestrator）

```sql
CREATE OR ALTER PROCEDURE dbo.sp_IngestWorldCupBatch
    @Teams      dbo.TeamTvp      READONLY,
    @Matches    dbo.MatchTvp     READONLY,
    @Markets    dbo.MarketTvp    READONLY,
    @Selections dbo.SelectionTvp READONLY,
    @Odds       dbo.OddsTvp      READONLY
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRAN;
            EXEC dbo.sp_UpsertTeams      @Teams;
            EXEC dbo.sp_UpsertMatches    @Matches;
            EXEC dbo.sp_UpsertMarkets    @Markets;
            EXEC dbo.sp_UpsertSelections @Selections;
            EXEC dbo.sp_RecordOddsIfChanged @Odds;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END
```

---

## 3. 呼叫方式（Match Service / Dapper）

`POST /api/fixtures/upsert` 收到批次後，把五個集合攤平成五個 `DataTable`（對應 TVP），一次呼叫 `sp_IngestWorldCupBatch`：

```csharp
var p = new DynamicParameters();
p.Add("@Teams",      teamsTable.AsTableValuedParameter("dbo.TeamTvp"));
p.Add("@Matches",    matchesTable.AsTableValuedParameter("dbo.MatchTvp"));
p.Add("@Markets",    marketsTable.AsTableValuedParameter("dbo.MarketTvp"));
p.Add("@Selections", selectionsTable.AsTableValuedParameter("dbo.SelectionTvp"));
p.Add("@Odds",       oddsTable.AsTableValuedParameter("dbo.OddsTvp"));
await conn.ExecuteAsync("dbo.sp_IngestWorldCupBatch", p,
                        commandType: CommandType.StoredProcedure);
```

> 一次網路往返 + 一個交易，完成整批世足賽事的賽程/玩法/選項/賠率同步。

---

## 4. 其他 SP（查詢端，之後用到再細化）

- `sp_GetMatches @TournamentExternalId` — 賽程列表（含主客隊名、開賽時間、狀態）
- `sp_GetMatchMarkets @MatchExternalId` — 某場的玩法 + 各選項最新賠率
- `sp_GetOddsHistory @SelectionExternalId` — 單一選項的賠率走勢（給走勢圖）
