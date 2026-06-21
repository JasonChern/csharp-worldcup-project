namespace WorldCup.Contracts;

/// <summary>Live 擷取 → Match Service 的批次上傳。重用 Fixtures（teams/matches/markets/odds），外加每場 live 狀態。</summary>
public record UpsertLiveRequest(
    IReadOnlyList<UpsertFixtureRequest> Fixtures,
    IReadOnlyList<MatchLiveState> LiveStates);

public record MatchLiveState(
    string MatchExternalId,
    int HomeScore,
    int AwayScore,
    string DomainStatus,        // Scheduled / Live / Ended
    string? LivePhase,          // raw, e.g. "1h"
    string? MatchMinute);       // e.g. "1:15"

public record LiveIngestResult(
    int MatchesUpdated,
    int ScoreChanges,
    int StatusChanges,
    int OddsSnapshotsInserted,
    int OddsChangedEvents,
    int MatchesEnded);
