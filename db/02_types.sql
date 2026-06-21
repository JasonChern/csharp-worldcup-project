-- WorldCup Live Hub — TVP (user-defined table types), bilingual
-- Drop+recreate guarded: types can't be altered, so drop if no dependent SP exists.
-- Run order: after 01_schema, before 03_procedures. (03 drops SPs first if needed.)

USE MatchDb;
GO

IF TYPE_ID('dbo.TeamTvp') IS NULL
CREATE TYPE dbo.TeamTvp AS TABLE (
    NameEn  NVARCHAR(100) NOT NULL,
    NameZh  NVARCHAR(100) NULL
);
GO

IF TYPE_ID('dbo.MatchTvp') IS NULL
CREATE TYPE dbo.MatchTvp AS TABLE (
    MatchExternalId       NVARCHAR(64)  NOT NULL,
    SourceGameId          NVARCHAR(64)  NULL,
    HomeTeamNameEn        NVARCHAR(100) NOT NULL,
    AwayTeamNameEn        NVARCHAR(100) NOT NULL,
    KickoffUtc            DATETIME2     NOT NULL,
    TournamentExternalId  NVARCHAR(32)  NOT NULL,
    TournamentNameEn      NVARCHAR(100) NULL,
    TournamentNameZh      NVARCHAR(100) NULL
);
GO

IF TYPE_ID('dbo.MarketTvp') IS NULL
CREATE TYPE dbo.MarketTvp AS TABLE (
    MarketExternalId  NVARCHAR(64)  NOT NULL,
    MatchExternalId   NVARCHAR(64)  NOT NULL,
    NameEn            NVARCHAR(100) NOT NULL,
    NameZh            NVARCHAR(100) NULL,
    MarketTypeCode    NVARCHAR(16)  NULL,
    Line              DECIMAL(6,2)  NULL
);
GO

IF TYPE_ID('dbo.SelectionTvp') IS NULL
CREATE TYPE dbo.SelectionTvp AS TABLE (
    SelectionExternalId  NVARCHAR(64)  NOT NULL,
    MarketExternalId     NVARCHAR(64)  NOT NULL,
    NameEn               NVARCHAR(100) NOT NULL,
    NameZh               NVARCHAR(100) NULL,
    Side                 CHAR(1)       NULL
);
GO

IF TYPE_ID('dbo.OddsTvp') IS NULL
CREATE TYPE dbo.OddsTvp AS TABLE (
    SelectionExternalId  NVARCHAR(64)  NOT NULL,
    Pd                   DECIMAL(10,2) NOT NULL,
    Pu                   DECIMAL(10,2) NOT NULL,
    DecimalOdds          DECIMAL(10,4) NOT NULL
);
GO

IF TYPE_ID('dbo.LiveStateTvp') IS NULL
CREATE TYPE dbo.LiveStateTvp AS TABLE (
    MatchExternalId NVARCHAR(64) NOT NULL,
    HomeScore       INT NOT NULL,
    AwayScore       INT NOT NULL,
    DomainStatus    VARCHAR(20) NOT NULL,    -- Scheduled/Live/Ended
    LivePhase       NVARCHAR(16) NULL,
    MatchMinute     NVARCHAR(8) NULL
);
GO
