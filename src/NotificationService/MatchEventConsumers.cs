using MassTransit;
using WorldCup.Contracts.Events;

namespace WorldCup.NotificationService;

public sealed class MatchScoreChangedConsumer : IConsumer<MatchScoreChanged>
{
    private readonly ILogger<MatchScoreChangedConsumer> _logger;
    public MatchScoreChangedConsumer(ILogger<MatchScoreChangedConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<MatchScoreChanged> context)
    {
        var e = context.Message;
        _logger.LogInformation(
            "⚽ 比分變動 | 賽事 {Match} | {OldH}:{OldA} → {NewH}:{NewA} | {Minute}",
            e.MatchExternalId, e.OldHome, e.OldAway, e.NewHome, e.NewAway, e.MatchMinute);
        return Task.CompletedTask;
    }
}

public sealed class MatchStatusChangedConsumer : IConsumer<MatchStatusChanged>
{
    private readonly ILogger<MatchStatusChangedConsumer> _logger;
    public MatchStatusChangedConsumer(ILogger<MatchStatusChangedConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<MatchStatusChanged> context)
    {
        var e = context.Message;
        _logger.LogInformation(
            "🟢 狀態變動 | 賽事 {Match} | {Old} → {New} ({Phase})",
            e.MatchExternalId, e.OldStatus, e.NewStatus, e.LivePhase);
        return Task.CompletedTask;
    }
}
