using System.Globalization;
using WorldCup.Contracts;
using WorldCup.IngestionJob.Fetching;

namespace WorldCup.IngestionJob.Mapping;

/// <summary>合併 en + zh（依語言無關 id 對應），過濾世足，換算賠率，產出上傳契約。</summary>
public static class FixtureMapper
{
    public static UpsertFixturesRequest Build(
        IReadOnlyList<SlGame> en, IReadOnlyList<SlGame> zh, string worldCupTournamentId)
    {
        var zhById = zh.ToDictionary(g => g.Id, g => g);

        var fixtures = new List<UpsertFixtureRequest>();
        foreach (var g in en.Where(x => x.TournamentId == worldCupTournamentId))
        {
            zhById.TryGetValue(g.Id, out var gz);

            var zhMarketsById = gz?.Markets.ToDictionary(m => m.Id, m => m)
                                ?? new Dictionary<string, SlMarket>();

            var markets = new List<UpsertMarketRequest>();
            foreach (var m in g.Markets)
            {
                zhMarketsById.TryGetValue(m.Id, out var mz);
                var zhChoicesById = mz?.Choices.ToDictionary(c => c.Id, c => c)
                                    ?? new Dictionary<string, SlChoice>();

                var selections = new List<UpsertSelectionRequest>();
                foreach (var c in m.Choices)
                {
                    if (!TryOdds(c, out var pd, out var pu, out var odds))
                        continue; // 無有效賠率的選項略過

                    zhChoicesById.TryGetValue(c.Id, out var cz);
                    selections.Add(new UpsertSelectionRequest(
                        SelectionExternalId: c.Id,
                        NameEn: c.Name,
                        NameZh: cz?.Name,
                        Side: c.Side,
                        Pd: pd,
                        Pu: pu,
                        DecimalOdds: odds));
                }

                markets.Add(new UpsertMarketRequest(
                    MarketExternalId: m.Id,
                    NameEn: m.Name,
                    NameZh: mz?.Name,
                    MarketTypeCode: m.MarketTypeCode,
                    Line: m.Line,
                    Selections: selections));
            }

            fixtures.Add(new UpsertFixtureRequest(
                MatchExternalId: string.IsNullOrWhiteSpace(g.ExternalRef) ? g.Id : g.ExternalRef!,
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
        }

        return new UpsertFixturesRequest(fixtures);
    }

    // odds = pu/pd + 1
    private static bool TryOdds(SlChoice c, out decimal pd, out decimal pu, out decimal odds)
    {
        pd = pu = odds = 0m;
        if (!decimal.TryParse(c.Pd, NumberStyles.Number, CultureInfo.InvariantCulture, out pd) ||
            !decimal.TryParse(c.Pu, NumberStyles.Number, CultureInfo.InvariantCulture, out pu) ||
            pd <= 0m)
            return false;
        odds = Math.Round(pu / pd + 1m, 4);
        return true;
    }
}
