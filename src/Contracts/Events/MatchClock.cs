namespace WorldCup.Contracts.Events;

/// <summary>比賽時鐘（live，每擷取週期一則，直接發布、不走 Outbox——掉一則下次校正即可）。
/// MatchMinute 為來源權威值（如 "51:03" 或 "45:00 +4:55"）；Running 表是否正在走鐘（中場/結束為 false）。</summary>
public record MatchClock(
    string MatchExternalId,
    string? MatchMinute,
    string? LivePhase,
    bool Running);
