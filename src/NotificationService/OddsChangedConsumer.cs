using MassTransit;
using WorldCup.Contracts.Events;

namespace WorldCup.NotificationService;

/// <summary>訂閱 OddsChanged，目前先印 log 證明 Pub/Sub 鏈路通。日後可改為產生個人化通知。</summary>
public sealed class OddsChangedConsumer : IConsumer<OddsChanged>
{
    private readonly ILogger<OddsChangedConsumer> _logger;

    public OddsChangedConsumer(ILogger<OddsChangedConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<OddsChanged> context)
    {
        var e = context.Message;
        _logger.LogInformation(
            "🔔 賠率變動 | 賽事 {Match} | 選項 {Sel}({Side}) {Name} | {Old} → {New}",
            e.MatchExternalId, e.SelectionExternalId, e.Side, e.SelectionNameEn, e.OldOdds, e.NewOdds);
        return Task.CompletedTask;
    }
}
