namespace WorldCup.Contracts.Events;

/// <summary>賠率變動事件（Outbox → RabbitMQ）。每個有變動的選項一則。</summary>
public record OddsChanged(
    string MatchExternalId,
    string MarketExternalId,
    string SelectionExternalId,
    string? Side,                 // H/A/D
    string SelectionNameEn,
    decimal OldOdds,
    decimal NewOdds,
    DateTime ChangedUtc);
