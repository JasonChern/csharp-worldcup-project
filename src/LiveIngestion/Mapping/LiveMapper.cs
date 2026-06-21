using System.Globalization;
using System.Text.Json;
using WorldCup.Contracts;
using WorldCup.LiveIngestion.Fetching;

namespace WorldCup.LiveIngestion.Mapping;

/// <summary>合併 en+zh、解析 ldss、換算賠率，產出 Live 上傳契約。</summary>
public static class LiveMapper
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static UpsertLiveRequest Build(
        IReadOnlyList<LiveGame> en, IReadOnlyList<LiveGame> zh, string worldCupTournamentId)
    {
        var zhById = zh.ToDictionary(g => g.Id, g => g);

        var fixtures = new List<UpsertFixtureRequest>();
        var liveStates = new List<MatchLiveState>();

        foreach (var g in en.Where(x => x.TournamentId == worldCupTournamentId))
        {
            var state = ParseLdss(g.LiveStateJson);
            var matchExtId = FirstNonEmpty(g.ExternalRef, state?.Id, g.Id);

            zhById.TryGetValue(g.Id, out var gz);
            var zhMarketsById = gz?.Markets.ToDictionary(m => m.Id, m => m) ?? new();

            var markets = new List<UpsertMarketRequest>();
            foreach (var m in g.Markets)
            {
                zhMarketsById.TryGetValue(m.Id, out var mz);
                var zhChoices = mz?.Choices.ToDictionary(c => c.Id, c => c) ?? new();

                var selections = new List<UpsertSelectionRequest>();
                foreach (var c in m.Choices)
                {
                    if (!TryOdds(c, out var pd, out var pu, out var odds)) continue;
                    zhChoices.TryGetValue(c.Id, out var cz);
                    selections.Add(new UpsertSelectionRequest(
                        c.Id, c.Name, cz?.Name, c.Side, pd, pu, odds));
                }

                markets.Add(new UpsertMarketRequest(
                    m.Id, m.Name, mz?.Name, m.MarketTypeCode, m.Line, selections));
            }

            fixtures.Add(new UpsertFixtureRequest(
                MatchExternalId: matchExtId,
                SourceGameId: g.Id,
                HomeTeamNameEn: g.HomeName,
                HomeTeamNameZh: gz?.HomeName,
                AwayTeamNameEn: g.AwayName,
                AwayTeamNameZh: gz?.AwayName,
                KickoffUtc: g.Kickoff.UtcDateTime,
                TournamentExternalId: g.TournamentId,
                TournamentNameEn: g.TournamentName,
                TournamentNameZh: gz?.TournamentName,
                Markets: markets));

            liveStates.Add(new MatchLiveState(
                MatchExternalId: matchExtId,
                HomeScore: state?.Scores?.Current?.Home ?? 0,
                AwayScore: state?.Scores?.Current?.Away ?? 0,
                DomainStatus: MapStatus(state?.Status),
                LivePhase: state?.Status,
                MatchMinute: state?.MatchTime));
        }

        return new UpsertLiveRequest(fixtures, liveStates);
    }

    private static LdssState? ParseLdss(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<LdssState>(json, JsonOpts); }
        catch (JsonException) { return null; }
    }

    // 狀態碼 → 領域狀態
    public static string MapStatus(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s is "ns" or "not_started" or "") return string.IsNullOrEmpty(s) ? "Live" : "Scheduled";
        if (s.Contains("end") || s is "ft" or "aet" or "ap" or "pen" or "closed" or "finished")
            return "Ended";
        return "Live"; // 1h / ht / 2h / 其他進行中
    }

    private static bool TryOdds(LiveChoice c, out decimal pd, out decimal pu, out decimal odds)
    {
        pd = pu = odds = 0m;
        if (!decimal.TryParse(c.Pd, NumberStyles.Number, CultureInfo.InvariantCulture, out pd) ||
            !decimal.TryParse(c.Pu, NumberStyles.Number, CultureInfo.InvariantCulture, out pu) ||
            pd <= 0m)
            return false;
        odds = Math.Round(pu / pd + 1m, 4);
        return true;
    }

    private static string FirstNonEmpty(params string?[] vals)
        => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
