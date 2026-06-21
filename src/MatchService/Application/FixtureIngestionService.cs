using Microsoft.Extensions.Logging;
using WorldCup.Contracts;

namespace WorldCup.MatchService.Application;

/// <summary>用例：驗證並寫入一批賽事。可在此加入跨實體規則（目前以基本驗證為主）。</summary>
public class FixtureIngestionService
{
    private readonly IFixtureRepository _repo;
    private readonly ILogger<FixtureIngestionService> _logger;

    public FixtureIngestionService(IFixtureRepository repo, ILogger<FixtureIngestionService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<IngestResult> IngestAsync(UpsertFixturesRequest request, CancellationToken ct)
    {
        if (request.Fixtures.Count == 0)
        {
            _logger.LogInformation("Ingest 收到空批次，略過。");
            return new IngestResult(0, 0, 0, 0);
        }

        foreach (var f in request.Fixtures)
        {
            if (string.IsNullOrWhiteSpace(f.MatchExternalId))
                throw new ArgumentException("MatchExternalId 不可為空。");
            if (string.IsNullOrWhiteSpace(f.HomeTeamNameEn) || string.IsNullOrWhiteSpace(f.AwayTeamNameEn))
                throw new ArgumentException($"賽事 {f.MatchExternalId} 缺少主/客隊英文名。");
        }

        var result = await _repo.IngestBatchAsync(request, ct);
        _logger.LogInformation(
            "Ingest 完成：賽事 {Matches}、玩法 {Markets}、選項 {Selections}、新增賠率快照 {Snapshots}、變動事件 {Events}",
            result.MatchesIn, result.MarketsIn, result.SelectionsIn, result.OddsSnapshotsInserted, result.OddsChangedEvents);
        return result;
    }

    public async Task<LiveIngestResult> IngestLiveAsync(UpsertLiveRequest request, CancellationToken ct)
    {
        var result = await _repo.IngestLiveAsync(request, ct);
        if (result.ScoreChanges > 0 || result.StatusChanges > 0 || result.OddsChangedEvents > 0 || result.MatchesEnded > 0)
            _logger.LogInformation(
                "Live 更新：場次 {Updated}、比分變動 {Score}、狀態變動 {Status}、賠率快照 {Odds}、結束 {Ended}",
                result.MatchesUpdated, result.ScoreChanges, result.StatusChanges, result.OddsSnapshotsInserted, result.MatchesEnded);
        return result;
    }
}
