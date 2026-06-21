-- WorldCup Live Hub — MatchDb schema (M1 + M2: fixtures + markets + odds, bilingual)
-- Idempotent: safe to re-run.

IF DB_ID('MatchDb') IS NULL
    CREATE DATABASE MatchDb;
GO
USE MatchDb;
GO

-- ---------- Teams ----------
IF OBJECT_ID('dbo.Teams') IS NULL
CREATE TABLE dbo.Teams (
    TeamId      UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    NameEn      NVARCHAR(100) NOT NULL UNIQUE,      -- canonical key (e.g. "Spain")
    NameZh      NVARCHAR(100) NULL,                 -- display (e.g. "西班牙")
    GroupCode   CHAR(1)       NULL                  -- A~H；deadball 無，留待補
);
GO

-- ---------- Matches ----------
IF OBJECT_ID('dbo.Matches') IS NULL
CREATE TABLE dbo.Matches (
    MatchId              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    ExternalId           NVARCHAR(64)  NOT NULL UNIQUE,   -- er (sr:match:...)
    SourceGameId         NVARCHAR(64)  NULL,              -- 運彩 game id (稽核)
    HomeTeamId           UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.Teams(TeamId),
    AwayTeamId           UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.Teams(TeamId),
    KickoffUtc           DATETIME2     NOT NULL,
    TournamentExternalId NVARCHAR(32)  NOT NULL,          -- ti
    TournamentNameEn     NVARCHAR(100) NULL,
    TournamentNameZh     NVARCHAR(100) NULL,
    Status               VARCHAR(20)   NOT NULL DEFAULT 'Scheduled', -- Scheduled/Live/Ended
    HomeScore            INT           NOT NULL DEFAULT 0,
    AwayScore            INT           NOT NULL DEFAULT 0
);
GO

-- ---------- Markets (玩法) ----------
IF OBJECT_ID('dbo.Markets') IS NULL
CREATE TABLE dbo.Markets (
    MarketId        BIGINT IDENTITY PRIMARY KEY,
    ExternalId      NVARCHAR(64) NOT NULL UNIQUE,         -- ms.id
    MatchId         UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.Matches(MatchId),
    NameEn          NVARCHAR(100) NOT NULL,               -- ms.name (en)
    NameZh          NVARCHAR(100) NULL,                   -- ms.name (zh)
    MarketTypeCode  NVARCHAR(16) NULL,                    -- ms.ti
    Line            DECIMAL(6,2) NULL,                    -- ms.mv
    UpdatedUtc      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ---------- MarketSelections (選項) ----------
IF OBJECT_ID('dbo.MarketSelections') IS NULL
CREATE TABLE dbo.MarketSelections (
    SelectionId BIGINT IDENTITY PRIMARY KEY,
    ExternalId  NVARCHAR(64) NOT NULL UNIQUE,             -- cs.id
    MarketId    BIGINT NOT NULL REFERENCES dbo.Markets(MarketId),
    NameEn      NVARCHAR(100) NOT NULL,                   -- cs.name (en)
    NameZh      NVARCHAR(100) NULL,                       -- cs.name (zh)
    Side        CHAR(1) NULL                              -- cs.v: H/A/D
);
GO

-- ---------- OddsSnapshots (賠率時序) ----------
IF OBJECT_ID('dbo.OddsSnapshots') IS NULL
CREATE TABLE dbo.OddsSnapshots (
    SnapshotId  BIGINT IDENTITY PRIMARY KEY,
    SelectionId BIGINT NOT NULL REFERENCES dbo.MarketSelections(SelectionId),
    Pd          DECIMAL(10,2) NOT NULL,
    Pu          DECIMAL(10,2) NOT NULL,
    DecimalOdds DECIMAL(10,4) NOT NULL,                   -- pu/pd + 1
    FetchedUtc  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Odds_Selection_Time')
    CREATE INDEX IX_Odds_Selection_Time ON dbo.OddsSnapshots(SelectionId, SnapshotId DESC);
GO

-- ---------- OutboxMessages (Outbox Pattern) ----------
IF OBJECT_ID('dbo.OutboxMessages') IS NULL
CREATE TABLE dbo.OutboxMessages (
    OutboxId     BIGINT IDENTITY PRIMARY KEY,
    EventType    NVARCHAR(200) NOT NULL,          -- 事件鑑別字（如 'OddsChanged'）
    Payload      NVARCHAR(MAX) NOT NULL,          -- JSON
    CreatedUtc   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ProcessedUtc DATETIME2 NULL                   -- NULL = 尚未發送
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Outbox_Unprocessed')
    CREATE INDEX IX_Outbox_Unprocessed ON dbo.OutboxMessages(OutboxId) WHERE ProcessedUtc IS NULL;
GO

-- ---------- Live 欄位（M-Live；冪等 ALTER） ----------
-- 前端需區分資料來源：Markets.Source = 'Pre'(賽前/deadball) 或 'Live'(即時/in-play)
IF COL_LENGTH('dbo.Markets', 'Source') IS NULL
    ALTER TABLE dbo.Markets ADD Source VARCHAR(10) NOT NULL CONSTRAINT DF_Markets_Source DEFAULT 'Pre';
GO
IF COL_LENGTH('dbo.Matches', 'LivePhase') IS NULL
    ALTER TABLE dbo.Matches ADD LivePhase NVARCHAR(16) NULL;        -- raw status, e.g. "1h"
GO
IF COL_LENGTH('dbo.Matches', 'MatchMinute') IS NULL
    ALTER TABLE dbo.Matches ADD MatchMinute NVARCHAR(8) NULL;       -- e.g. "1:15"
GO
IF COL_LENGTH('dbo.Matches', 'LastSeenLiveUtc') IS NULL
    ALTER TABLE dbo.Matches ADD LastSeenLiveUtc DATETIME2 NULL;     -- 最近一次出現在 live feed
GO
