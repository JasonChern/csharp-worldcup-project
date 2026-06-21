using MassTransit;
using Microsoft.AspNetCore.SignalR;
using WorldCup.Contracts.Events;
using WorldCup.RealtimeService.Hubs;

namespace WorldCup.RealtimeService.Consumers;

// 注意：consumer 類別名稱需與其他服務（NotificationService）不同，
// 否則 MassTransit 產生的佇列名會相同而互搶訊息（破壞 fan-out）。

public sealed class ScoreBroadcastConsumer : IConsumer<MatchScoreChanged>
{
    private readonly IHubContext<LiveHub> _hub;
    private readonly ILogger<ScoreBroadcastConsumer> _logger;

    public ScoreBroadcastConsumer(IHubContext<LiveHub> hub, ILogger<ScoreBroadcastConsumer> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<MatchScoreChanged> context)
    {
        var e = context.Message;
        await _hub.Clients.All.SendAsync("ScoreUpdated", new
        {
            matchExternalId = e.MatchExternalId,
            home = e.NewHome,
            away = e.NewAway,
            minute = e.MatchMinute
        });
        _logger.LogInformation("→ SignalR ScoreUpdated {Match} {H}:{A}", e.MatchExternalId, e.NewHome, e.NewAway);
    }
}

public sealed class OddsBroadcastConsumer : IConsumer<OddsChanged>
{
    private readonly IHubContext<LiveHub> _hub;
    public OddsBroadcastConsumer(IHubContext<LiveHub> hub) => _hub = hub;

    public Task Consume(ConsumeContext<OddsChanged> context)
    {
        var e = context.Message;
        return _hub.Clients.All.SendAsync("OddsUpdated", new
        {
            matchExternalId = e.MatchExternalId,
            selectionExternalId = e.SelectionExternalId,
            newOdds = e.NewOdds
        });
    }
}

public sealed class ClockBroadcastConsumer : IConsumer<MatchClock>
{
    private readonly IHubContext<LiveHub> _hub;
    public ClockBroadcastConsumer(IHubContext<LiveHub> hub) => _hub = hub;

    public Task Consume(ConsumeContext<MatchClock> context)
    {
        var e = context.Message;
        return _hub.Clients.All.SendAsync("ClockUpdated", new
        {
            matchExternalId = e.MatchExternalId,
            matchMinute = e.MatchMinute,
            livePhase = e.LivePhase,
            running = e.Running
        });
    }
}

public sealed class StatusBroadcastConsumer : IConsumer<MatchStatusChanged>
{
    private readonly IHubContext<LiveHub> _hub;
    private readonly ILogger<StatusBroadcastConsumer> _logger;

    public StatusBroadcastConsumer(IHubContext<LiveHub> hub, ILogger<StatusBroadcastConsumer> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<MatchStatusChanged> context)
    {
        var e = context.Message;
        await _hub.Clients.All.SendAsync("StatusUpdated", new
        {
            matchExternalId = e.MatchExternalId,
            status = e.NewStatus,
            livePhase = e.LivePhase
        });
        _logger.LogInformation("→ SignalR StatusUpdated {Match} {Status}", e.MatchExternalId, e.NewStatus);
    }
}
