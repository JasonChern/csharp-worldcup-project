namespace WorldCup.Contracts;

/// <summary>Ingestion → Match Service 的批次上傳契約（雙語）。</summary>
public record UpsertFixturesRequest(IReadOnlyList<UpsertFixtureRequest> Fixtures);

public record UpsertFixtureRequest(
    string MatchExternalId,          // er, e.g. "sr:match:66456998"
    string? SourceGameId,            // id, e.g. "3474285.1"
    string HomeTeamNameEn,
    string? HomeTeamNameZh,
    string AwayTeamNameEn,
    string? AwayTeamNameZh,
    DateTime KickoffUtc,
    string TournamentExternalId,     // ti
    string? TournamentNameEn,
    string? TournamentNameZh,
    IReadOnlyList<UpsertMarketRequest> Markets);

public record UpsertMarketRequest(
    string MarketExternalId,         // ms.id
    string NameEn,
    string? NameZh,
    string? MarketTypeCode,          // ms.ti
    decimal? Line,                   // ms.mv
    IReadOnlyList<UpsertSelectionRequest> Selections);

public record UpsertSelectionRequest(
    string SelectionExternalId,      // cs.id
    string NameEn,
    string? NameZh,
    string? Side,                    // H/A/D
    decimal Pd,
    decimal Pu,
    decimal DecimalOdds);            // pu/pd + 1

/// <summary>批次寫入結果。</summary>
public record IngestResult(
    int MatchesIn,
    int MarketsIn,
    int SelectionsIn,
    int OddsSnapshotsInserted);
