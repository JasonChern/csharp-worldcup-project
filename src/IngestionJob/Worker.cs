using WorldCup.IngestionJob.Fetching;
using WorldCup.IngestionJob.Mapping;
using WorldCup.IngestionJob.Publishing;

namespace WorldCup.IngestionJob;

/// <summary>定時：抓 en+zh → 合併過濾世足 → 換算賠率 → POST Match Service。</summary>
public sealed class Worker : BackgroundService
{
    private readonly ISportsLotteryClient _lottery;
    private readonly IMatchServiceClient _matchService;
    private readonly IngestionOptions _opts;
    private readonly ILogger<Worker> _logger;

    public Worker(ISportsLotteryClient lottery, IMatchServiceClient matchService,
        IngestionOptions opts, ILogger<Worker> logger)
    {
        _lottery = lottery;
        _matchService = matchService;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(30, _opts.IntervalSeconds));
        using var timer = new PeriodicTimer(period);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "擷取循環發生錯誤，將於下次排程重試。");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        _logger.LogInformation("開始擷取運彩賽前資料…");
        var en = await _lottery.FetchEnglishAsync(ct);
        var zh = await _lottery.FetchChineseAsync(ct);

        var request = FixtureMapper.Build(en, zh, _opts.WorldCupTournamentId);
        _logger.LogInformation("過濾世足({Ti})：{Count} 場，準備上傳。",
            _opts.WorldCupTournamentId, request.Fixtures.Count);

        if (request.Fixtures.Count == 0) return;

        var result = await _matchService.UpsertFixturesAsync(request, ct);
        if (result is not null)
            _logger.LogInformation(
                "上傳完成：賽事 {M}、玩法 {K}、選項 {S}、新增賠率快照 {O}、變動事件 {E}",
                result.MatchesIn, result.MarketsIn, result.SelectionsIn, result.OddsSnapshotsInserted, result.OddsChangedEvents);
    }
}
