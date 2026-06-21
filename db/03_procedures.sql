-- WorldCup Live Hub — Stored Procedures (TVP batch, set-based, bilingual)
USE MatchDb;
GO

CREATE OR ALTER PROCEDURE dbo.sp_UpsertTeams
    @Teams dbo.TeamTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.Teams AS t
    USING (
        -- 同一 NameEn 可能在多場出現，取一個非空 NameZh
        SELECT NameEn, MAX(NameZh) AS NameZh
        FROM @Teams GROUP BY NameEn
    ) AS src
        ON t.NameEn = src.NameEn
    WHEN MATCHED AND src.NameZh IS NOT NULL THEN
        UPDATE SET t.NameZh = src.NameZh
    WHEN NOT MATCHED THEN
        INSERT (NameEn, NameZh) VALUES (src.NameEn, src.NameZh);
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_UpsertMatches
    @Matches dbo.MatchTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.Matches AS m
    USING (
        SELECT x.MatchExternalId, x.SourceGameId, x.KickoffUtc,
               x.TournamentExternalId, x.TournamentNameEn, x.TournamentNameZh,
               h.TeamId AS HomeTeamId, a.TeamId AS AwayTeamId
        FROM @Matches x
        JOIN dbo.Teams h ON h.NameEn = x.HomeTeamNameEn
        JOIN dbo.Teams a ON a.NameEn = x.AwayTeamNameEn
    ) AS src
        ON m.ExternalId = src.MatchExternalId
    WHEN MATCHED THEN
        UPDATE SET m.KickoffUtc = src.KickoffUtc,            -- 只同步賽程
                   m.TournamentNameEn = src.TournamentNameEn,
                   m.TournamentNameZh = src.TournamentNameZh
    WHEN NOT MATCHED THEN
        INSERT (ExternalId, SourceGameId, HomeTeamId, AwayTeamId, KickoffUtc,
                TournamentExternalId, TournamentNameEn, TournamentNameZh, Status)
        VALUES (src.MatchExternalId, src.SourceGameId, src.HomeTeamId, src.AwayTeamId, src.KickoffUtc,
                src.TournamentExternalId, src.TournamentNameEn, src.TournamentNameZh, 'Scheduled');
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_UpsertMarkets
    @Markets dbo.MarketTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.Markets AS k
    USING (
        SELECT x.MarketExternalId, x.NameEn, x.NameZh, x.MarketTypeCode, x.Line, m.MatchId
        FROM @Markets x
        JOIN dbo.Matches m ON m.ExternalId = x.MatchExternalId
    ) AS src
        ON k.ExternalId = src.MarketExternalId
    WHEN MATCHED THEN
        UPDATE SET k.NameEn = src.NameEn, k.NameZh = src.NameZh,
                   k.MarketTypeCode = src.MarketTypeCode, k.Line = src.Line,
                   k.UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (ExternalId, MatchId, NameEn, NameZh, MarketTypeCode, Line)
        VALUES (src.MarketExternalId, src.MatchId, src.NameEn, src.NameZh, src.MarketTypeCode, src.Line);
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_UpsertSelections
    @Selections dbo.SelectionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.MarketSelections AS s
    USING (
        SELECT x.SelectionExternalId, x.NameEn, x.NameZh, x.Side, k.MarketId
        FROM @Selections x
        JOIN dbo.Markets k ON k.ExternalId = x.MarketExternalId
    ) AS src
        ON s.ExternalId = src.SelectionExternalId
    WHEN MATCHED THEN
        UPDATE SET s.NameEn = src.NameEn, s.NameZh = src.NameZh, s.Side = src.Side
    WHEN NOT MATCHED THEN
        INSERT (ExternalId, MarketId, NameEn, NameZh, Side)
        VALUES (src.SelectionExternalId, src.MarketId, src.NameEn, src.NameZh, src.Side);
END
GO

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
    WHERE prev.DecimalOdds IS NULL
       OR prev.DecimalOdds <> o.DecimalOdds;

    SELECT @@ROWCOUNT AS SnapshotsInserted;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_IngestWorldCupBatch
    @Teams      dbo.TeamTvp      READONLY,
    @Matches    dbo.MatchTvp     READONLY,
    @Markets    dbo.MarketTvp    READONLY,
    @Selections dbo.SelectionTvp READONLY,
    @Odds       dbo.OddsTvp      READONLY
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @oddsInserted INT = 0;
    BEGIN TRY
        BEGIN TRAN;
            EXEC dbo.sp_UpsertTeams      @Teams;
            EXEC dbo.sp_UpsertMatches    @Matches;
            EXEC dbo.sp_UpsertMarkets    @Markets;
            EXEC dbo.sp_UpsertSelections @Selections;

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
            WHERE prev.DecimalOdds IS NULL OR prev.DecimalOdds <> o.DecimalOdds;
            SET @oddsInserted = @@ROWCOUNT;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH

    SELECT
        (SELECT COUNT(*) FROM @Matches) AS MatchesIn,
        (SELECT COUNT(*) FROM @Markets) AS MarketsIn,
        (SELECT COUNT(*) FROM @Selections) AS SelectionsIn,
        @oddsInserted AS OddsSnapshotsInserted;
END
GO

-- ---------- Query-side ----------
CREATE OR ALTER PROCEDURE dbo.sp_GetMatches
    @TournamentExternalId NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT m.ExternalId, m.KickoffUtc, m.Status, m.HomeScore, m.AwayScore,
           m.TournamentNameEn, m.TournamentNameZh,
           h.NameEn AS HomeTeamEn, h.NameZh AS HomeTeamZh,
           a.NameEn AS AwayTeamEn, a.NameZh AS AwayTeamZh
    FROM dbo.Matches m
    JOIN dbo.Teams h ON h.TeamId = m.HomeTeamId
    JOIN dbo.Teams a ON a.TeamId = m.AwayTeamId
    WHERE (@TournamentExternalId IS NULL OR m.TournamentExternalId = @TournamentExternalId)
    ORDER BY m.KickoffUtc;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_GetMatchMarkets
    @MatchExternalId NVARCHAR(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT k.ExternalId AS MarketExternalId, k.NameEn AS MarketNameEn, k.NameZh AS MarketNameZh,
           k.Line, s.ExternalId AS SelectionExternalId,
           s.NameEn AS SelectionNameEn, s.NameZh AS SelectionNameZh, s.Side,
           o.Pd, o.Pu, o.DecimalOdds, o.FetchedUtc
    FROM dbo.Matches m
    JOIN dbo.Markets k ON k.MatchId = m.MatchId
    JOIN dbo.MarketSelections s ON s.MarketId = k.MarketId
    OUTER APPLY (
        SELECT TOP 1 os.Pd, os.Pu, os.DecimalOdds, os.FetchedUtc
        FROM dbo.OddsSnapshots os
        WHERE os.SelectionId = s.SelectionId
        ORDER BY os.SnapshotId DESC
    ) o
    WHERE m.ExternalId = @MatchExternalId
    ORDER BY k.MarketId, s.SelectionId;
END
GO
