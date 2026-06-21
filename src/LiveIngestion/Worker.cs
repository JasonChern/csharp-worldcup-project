using MassTransit;
using WorldCup.Contracts.Events;
using WorldCup.LiveIngestion.Fetching;
using WorldCup.LiveIngestion.Mapping;
using WorldCup.LiveIngestion.Publishing;

namespace WorldCup.LiveIngestion;

/// <summary>高頻：抓 Live en+zh → 解析 ldss + in-play 賠率 → POST /api/live/upsert。
/// 即使目前無進行中場次也會 POST（空批次），讓 Match Service 端跑「結束對帳」。</summary>
public sealed class Worker : BackgroundService
{
    private readonly ILiveLotteryClient _lottery;
    private readonly IMatchServiceClient _matchService;
    private readonly IBus _bus;
    private readonly LiveOptions _opts;
    private readonly ILogger<Worker> _logger;

    public Worker(ILiveLotteryClient lottery, IMatchServiceClient matchService,
        IBus bus, LiveOptions opts, ILogger<Worker> logger)
    {
        _lottery = lottery;
        _matchService = matchService;
        _bus = bus;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(5, _opts.IntervalSeconds));
        using var timer = new PeriodicTimer(period);
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Live 擷取循環錯誤，下次重試。"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var en = await _lottery.FetchEnglishAsync(ct);
        var zh = await _lottery.FetchChineseAsync(ct);

        var request = LiveMapper.Build(en, zh, _opts.WorldCupTournamentId);

        // 時鐘：每輪對每場進行中的賽事發一則權威分鐘（前端據此校準後每秒平滑走鐘）
        foreach (var ls in request.LiveStates)
        {
            var running = ls.DomainStatus == "Live" && !IsPaused(ls.LivePhase);
            await _bus.Publish(new MatchClock(ls.MatchExternalId, ls.MatchMinute, ls.LivePhase, running), ct);
        }

        var result = await _matchService.UpsertLiveAsync(request, ct);
        if (result is not null && (result.ScoreChanges > 0 || result.StatusChanges > 0
            || result.OddsChangedEvents > 0 || result.MatchesEnded > 0))
        {
            _logger.LogInformation(
                "Live：進行中 {Live} 場 | 比分變動 {Score}、狀態變動 {Status}、賠率變動 {Odds}、結束 {Ended}",
                request.LiveStates.Count, result.ScoreChanges, result.StatusChanges,
                result.OddsChangedEvents, result.MatchesEnded);
        }
        else
        {
            _logger.LogDebug("Live：進行中 {Live} 場，無變動。", request.LiveStates.Count);
        }
    }

    // 非走鐘階段：中場(paused/ht)、暫停、結束等
    private static bool IsPaused(string? phase)
    {
        var p = (phase ?? "").ToLowerInvariant();
        return p.Contains("paus") || p is "ht" or "halftime" or "break" or "interrupted";
    }
}
