using WorldCup.Contracts;

namespace WorldCup.MatchService.Application;

/// <summary>寫入 port：把整批賽事/玩法/賠率交給持久層（內部走 TVP + sp_IngestWorldCupBatch）。</summary>
public interface IFixtureRepository
{
    Task<IngestResult> IngestBatchAsync(UpsertFixturesRequest request, CancellationToken ct);
    Task<LiveIngestResult> IngestLiveAsync(UpsertLiveRequest request, CancellationToken ct);
}

/// <summary>讀取 port。</summary>
public interface IMatchReadRepository
{
    Task<IReadOnlyList<MatchSummary>> GetMatchesAsync(string? tournamentExternalId, CancellationToken ct);
    Task<IReadOnlyList<MarketWithOdds>> GetMatchMarketsAsync(string matchExternalId, CancellationToken ct);
}

/// <summary>DB 初始化 port（執行 schema/types/procedures 腳本）。</summary>
public interface IDbInitializer
{
    Task InitializeAsync(CancellationToken ct);
}

// ---- 讀模型 ----
public record MatchSummary(
    string ExternalId,
    DateTime KickoffUtc,
    string Status,
    int HomeScore,
    int AwayScore,
    string? LivePhase,
    string? MatchMinute,
    string? TournamentNameEn,
    string? TournamentNameZh,
    string HomeTeamEn,
    string? HomeTeamZh,
    string AwayTeamEn,
    string? AwayTeamZh);

public record MarketWithOdds(
    string MarketExternalId,
    string MarketNameEn,
    string? MarketNameZh,
    decimal? Line,
    string Source,                  // 'Pre'(賽前) / 'Live'(即時)
    string SelectionExternalId,
    string SelectionNameEn,
    string? SelectionNameZh,
    string? Side,
    decimal? Pd,
    decimal? Pu,
    decimal? DecimalOdds,
    DateTime? FetchedUtc);
