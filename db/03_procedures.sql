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
    DECLARE @oddsInserted INT = 0, @oddsChanged INT = 0;
    BEGIN TRY
        BEGIN TRAN;
            EXEC dbo.sp_UpsertTeams      @Teams;
            EXEC dbo.sp_UpsertMatches    @Matches;
            EXEC dbo.sp_UpsertMarkets    @Markets;
            EXEC dbo.sp_UpsertSelections @Selections;

            -- 比對每個選項的最新賠率（prev）與本次（new）
            SELECT s.SelectionId, s.ExternalId AS SelExt, s.NameEn AS SelName, s.Side,
                   k.ExternalId AS MarketExt, mt.ExternalId AS MatchExt,
                   o.Pd, o.Pu, o.DecimalOdds AS NewOdds, prev.DecimalOdds AS PrevOdds
            INTO #calc
            FROM @Odds o
            JOIN dbo.MarketSelections s ON s.ExternalId = o.SelectionExternalId
            JOIN dbo.Markets k  ON k.MarketId  = s.MarketId
            JOIN dbo.Matches mt ON mt.MatchId  = k.MatchId
            OUTER APPLY (
                SELECT TOP 1 last.DecimalOdds
                FROM dbo.OddsSnapshots last
                WHERE last.SelectionId = s.SelectionId
                ORDER BY last.SnapshotId DESC
            ) prev;

            -- 1) 快照：首次或有變動都寫
            INSERT INTO dbo.OddsSnapshots (SelectionId, Pd, Pu, DecimalOdds)
            SELECT SelectionId, Pd, Pu, NewOdds
            FROM #calc
            WHERE PrevOdds IS NULL OR PrevOdds <> NewOdds;
            SET @oddsInserted = @@ROWCOUNT;

            -- 2) Outbox：只在「真正變動」時發（prev 已存在且不同），與寫入同交易
            INSERT INTO dbo.OutboxMessages (EventType, Payload)
            SELECT 'OddsChanged',
                   (SELECT c.MatchExt        AS matchExternalId,
                           c.MarketExt       AS marketExternalId,
                           c.SelExt          AS selectionExternalId,
                           c.Side            AS side,
                           c.SelName         AS selectionNameEn,
                           c.PrevOdds        AS oldOdds,
                           c.NewOdds         AS newOdds,
                           SYSUTCDATETIME()  AS changedUtc
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
            FROM #calc c
            WHERE c.PrevOdds IS NOT NULL AND c.PrevOdds <> c.NewOdds;
            SET @oddsChanged = @@ROWCOUNT;

            DROP TABLE #calc;
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
        @oddsInserted AS OddsSnapshotsInserted,
        @oddsChanged  AS OddsChangedEvents;
END
GO

-- ---------- Outbox dequeue / mark ----------
CREATE OR ALTER PROCEDURE dbo.sp_GetUnprocessedOutbox
    @Take INT = 100
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Take) OutboxId, EventType, Payload, CreatedUtc
    FROM dbo.OutboxMessages
    WHERE ProcessedUtc IS NULL
    ORDER BY OutboxId;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_MarkOutboxProcessed
    @OutboxId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.OutboxMessages
    SET ProcessedUtc = SYSUTCDATETIME()
    WHERE OutboxId = @OutboxId AND ProcessedUtc IS NULL;
END
GO

-- ---------- Query-side ----------
CREATE OR ALTER PROCEDURE dbo.sp_GetMatches
    @TournamentExternalId NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT m.ExternalId, m.KickoffUtc, m.Status, m.HomeScore, m.AwayScore,
           m.LivePhase, m.MatchMinute,
           m.TournamentNameEn, m.TournamentNameZh,
           h.NameEn AS HomeTeamEn, h.NameZh AS HomeTeamZh,
           a.NameEn AS AwayTeamEn, a.NameZh AS AwayTeamZh,
           ml.HomeSelId, ml.HomeOdds, ml.DrawSelId, ml.DrawOdds, ml.AwaySelId, ml.AwayOdds
    FROM dbo.Matches m
    JOIN dbo.Teams h ON h.TeamId = m.HomeTeamId
    JOIN dbo.Teams a ON a.TeamId = m.AwayTeamId
    OUTER APPLY (
        -- 主盤 1X2（Money Line / 不讓分）三選項的最新賠率與選項 id
        SELECT
            MAX(CASE WHEN s.Side = 'H' THEN s.ExternalId END) AS HomeSelId,
            MAX(CASE WHEN s.Side = 'H' THEN o.DecimalOdds END) AS HomeOdds,
            MAX(CASE WHEN s.Side = 'D' THEN s.ExternalId END) AS DrawSelId,
            MAX(CASE WHEN s.Side = 'D' THEN o.DecimalOdds END) AS DrawOdds,
            MAX(CASE WHEN s.Side = 'A' THEN s.ExternalId END) AS AwaySelId,
            MAX(CASE WHEN s.Side = 'A' THEN o.DecimalOdds END) AS AwayOdds
        FROM dbo.Markets k
        JOIN dbo.MarketSelections s ON s.MarketId = k.MarketId
        OUTER APPLY (
            SELECT TOP 1 os.DecimalOdds FROM dbo.OddsSnapshots os
            WHERE os.SelectionId = s.SelectionId ORDER BY os.SnapshotId DESC
        ) o
        WHERE k.MatchId = m.MatchId AND k.NameEn = '1x2' AND s.Side IN ('H','D','A')
    ) ml
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
           k.Line, k.Source, s.ExternalId AS SelectionExternalId,
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

-- ========== Live ingestion ==========
-- 重用 teams/matches/markets/selections/odds 的 upsert，外加 live 狀態(比分/狀態)更新、
-- in-play 來源標記(Source='Live')、結束對帳。全部一個交易。
CREATE OR ALTER PROCEDURE dbo.sp_IngestLiveBatch
    @Teams      dbo.TeamTvp      READONLY,
    @Matches    dbo.MatchTvp     READONLY,
    @Markets    dbo.MarketTvp    READONLY,
    @Selections dbo.SelectionTvp READONLY,
    @Odds       dbo.OddsTvp      READONLY,
    @LiveStates dbo.LiveStateTvp READONLY,
    @StaleGraceMinutes INT = 2
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @oddsInserted INT = 0, @oddsChanged INT = 0,
            @scoreChanges INT = 0, @statusChanges INT = 0, @ended INT = 0;
    BEGIN TRY
        BEGIN TRAN;
            EXEC dbo.sp_UpsertTeams      @Teams;
            EXEC dbo.sp_UpsertMatches    @Matches;
            EXEC dbo.sp_UpsertMarkets    @Markets;
            EXEC dbo.sp_UpsertSelections @Selections;

            -- in-play 玩法標記來源
            UPDATE k SET k.Source = 'Live'
            FROM dbo.Markets k
            JOIN @Markets x ON x.MarketExternalId = k.ExternalId;

            -- ---- Live 狀態：比對現存 vs 本次 ----
            SELECT m.MatchId, m.ExternalId,
                   m.HomeScore AS OldH, m.AwayScore AS OldA, m.Status AS OldStatus, m.LivePhase AS OldPhase,
                   ls.HomeScore AS NewH, ls.AwayScore AS NewA, ls.DomainStatus AS NewStatus,
                   ls.LivePhase AS NewPhase, ls.MatchMinute AS NewMinute
            INTO #live
            FROM @LiveStates ls
            JOIN dbo.Matches m ON m.ExternalId = ls.MatchExternalId;

            UPDATE m
            SET m.HomeScore = l.NewH, m.AwayScore = l.NewA, m.Status = l.NewStatus,
                m.LivePhase = l.NewPhase, m.MatchMinute = l.NewMinute,
                m.LastSeenLiveUtc = SYSUTCDATETIME()
            FROM dbo.Matches m JOIN #live l ON l.MatchId = m.MatchId;

            -- 比分變動事件
            INSERT INTO dbo.OutboxMessages (EventType, Payload)
            SELECT 'MatchScoreChanged',
                   (SELECT l.ExternalId AS matchExternalId, l.OldH AS oldHome, l.OldA AS oldAway,
                           l.NewH AS newHome, l.NewA AS newAway, l.NewMinute AS matchMinute,
                           SYSUTCDATETIME() AS changedUtc
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
            FROM #live l
            WHERE l.NewH <> l.OldH OR l.NewA <> l.OldA;
            SET @scoreChanges = @@ROWCOUNT;

            -- 狀態/階段變動事件
            INSERT INTO dbo.OutboxMessages (EventType, Payload)
            SELECT 'MatchStatusChanged',
                   (SELECT l.ExternalId AS matchExternalId, l.OldStatus AS oldStatus,
                           l.NewStatus AS newStatus, l.NewPhase AS livePhase,
                           SYSUTCDATETIME() AS changedUtc
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
            FROM #live l
            WHERE l.NewStatus <> l.OldStatus OR ISNULL(l.NewPhase,'') <> ISNULL(l.OldPhase,'');
            SET @statusChanges = @@ROWCOUNT;

            -- ---- in-play 賠率：變動才寫快照 + 事件（同 M3） ----
            SELECT s.SelectionId, s.ExternalId AS SelExt, s.NameEn AS SelName, s.Side,
                   k.ExternalId AS MarketExt, mt.ExternalId AS MatchExt,
                   o.Pd, o.Pu, o.DecimalOdds AS NewOdds, prev.DecimalOdds AS PrevOdds
            INTO #calc
            FROM @Odds o
            JOIN dbo.MarketSelections s ON s.ExternalId = o.SelectionExternalId
            JOIN dbo.Markets k  ON k.MarketId = s.MarketId
            JOIN dbo.Matches mt ON mt.MatchId = k.MatchId
            OUTER APPLY (
                SELECT TOP 1 last.DecimalOdds FROM dbo.OddsSnapshots last
                WHERE last.SelectionId = s.SelectionId ORDER BY last.SnapshotId DESC
            ) prev;

            INSERT INTO dbo.OddsSnapshots (SelectionId, Pd, Pu, DecimalOdds)
            SELECT SelectionId, Pd, Pu, NewOdds FROM #calc
            WHERE PrevOdds IS NULL OR PrevOdds <> NewOdds;
            SET @oddsInserted = @@ROWCOUNT;

            INSERT INTO dbo.OutboxMessages (EventType, Payload)
            SELECT 'OddsChanged',
                   (SELECT c.MatchExt AS matchExternalId, c.MarketExt AS marketExternalId,
                           c.SelExt AS selectionExternalId, c.Side AS side, c.SelName AS selectionNameEn,
                           c.PrevOdds AS oldOdds, c.NewOdds AS newOdds, SYSUTCDATETIME() AS changedUtc
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
            FROM #calc c
            WHERE c.PrevOdds IS NOT NULL AND c.PrevOdds <> c.NewOdds;
            SET @oddsChanged = @@ROWCOUNT;

            -- ---- 結束對帳：曾 Live 但已超過寬限未再出現 → 標 Ended ----
            SELECT m.MatchId, m.ExternalId, m.Status AS OldStatus
            INTO #stale
            FROM dbo.Matches m
            WHERE m.Status = 'Live'
              AND (m.LastSeenLiveUtc IS NULL
                   OR m.LastSeenLiveUtc < DATEADD(MINUTE, -@StaleGraceMinutes, SYSUTCDATETIME()));

            UPDATE m SET m.Status = 'Ended'
            FROM dbo.Matches m JOIN #stale st ON st.MatchId = m.MatchId;

            INSERT INTO dbo.OutboxMessages (EventType, Payload)
            SELECT 'MatchStatusChanged',
                   (SELECT st.ExternalId AS matchExternalId, st.OldStatus AS oldStatus,
                           'Ended' AS newStatus, NULL AS livePhase, SYSUTCDATETIME() AS changedUtc
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
            FROM #stale st;
            SET @ended = @@ROWCOUNT;
            SET @statusChanges = @statusChanges + @ended;

            DROP TABLE #live; DROP TABLE #calc; DROP TABLE #stale;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH

    SELECT
        (SELECT COUNT(*) FROM @LiveStates) AS MatchesUpdated,
        @scoreChanges  AS ScoreChanges,
        @statusChanges AS StatusChanges,
        @oddsInserted  AS OddsSnapshotsInserted,
        @oddsChanged   AS OddsChangedEvents,
        @ended         AS MatchesEnded;
END
GO

-- ---------- 賠率走勢（時序） ----------
CREATE OR ALTER PROCEDURE dbo.sp_GetOddsHistory
    @SelectionExternalId NVARCHAR(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT os.DecimalOdds, os.FetchedUtc
    FROM dbo.OddsSnapshots os
    JOIN dbo.MarketSelections s ON s.SelectionId = os.SelectionId
    WHERE s.ExternalId = @SelectionExternalId
    ORDER BY os.SnapshotId;
END
GO
