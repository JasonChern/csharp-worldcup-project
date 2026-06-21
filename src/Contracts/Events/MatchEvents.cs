namespace WorldCup.Contracts.Events;

/// <summary>比分變動（live）。進球由比分差推斷，無射手資訊。</summary>
public record MatchScoreChanged(
    string MatchExternalId,
    int OldHome,
    int OldAway,
    int NewHome,
    int NewAway,
    string? MatchMinute,
    DateTime ChangedUtc);

/// <summary>賽事狀態/階段變動（含 Scheduled→Live、1h→ht、→Ended）。</summary>
public record MatchStatusChanged(
    string MatchExternalId,
    string OldStatus,
    string NewStatus,
    string? LivePhase,
    DateTime ChangedUtc);
