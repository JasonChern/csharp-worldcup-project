using System.Data;
using Dapper;
using WorldCup.Contracts;
using WorldCup.MatchService.Application;

namespace WorldCup.MatchService.Infrastructure;

/// <summary>把整批請求攤平成 5 個 TVP（DataTable），一次呼叫 dbo.sp_IngestWorldCupBatch。</summary>
public sealed class SqlFixtureRepository : IFixtureRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlFixtureRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<IngestResult> IngestBatchAsync(UpsertFixturesRequest request, CancellationToken ct)
    {
        var p = BuildFixtureParams(request.Fixtures);
        await using var conn = _factory.Create();
        var cmd = new CommandDefinition("dbo.sp_IngestWorldCupBatch", p,
            commandType: CommandType.StoredProcedure, cancellationToken: ct);
        return await conn.QueryFirstAsync<IngestResult>(cmd);
    }

    public async Task<LiveIngestResult> IngestLiveAsync(UpsertLiveRequest request, CancellationToken ct)
    {
        var p = BuildFixtureParams(request.Fixtures);

        var live = NewLiveStateTable();
        foreach (var s in request.LiveStates)
            live.Rows.Add(s.MatchExternalId, s.HomeScore, s.AwayScore, s.DomainStatus,
                (object?)s.LivePhase ?? DBNull.Value, (object?)s.MatchMinute ?? DBNull.Value);
        p.Add("@LiveStates", live.AsTableValuedParameter("dbo.LiveStateTvp"));

        await using var conn = _factory.Create();
        var cmd = new CommandDefinition("dbo.sp_IngestLiveBatch", p,
            commandType: CommandType.StoredProcedure, cancellationToken: ct);
        return await conn.QueryFirstAsync<LiveIngestResult>(cmd);
    }

    // 共用：把 fixtures 攤平成 teams/matches/markets/selections/odds 五個 TVP
    private static DynamicParameters BuildFixtureParams(IReadOnlyList<UpsertFixtureRequest> fixtures)
    {
        var teams = NewTeamsTable();
        var matches = NewMatchesTable();
        var markets = NewMarketsTable();
        var selections = NewSelectionsTable();
        var odds = NewOddsTable();

        var seenTeams = new HashSet<string>();
        foreach (var f in fixtures)
        {
            AddTeam(teams, seenTeams, f.HomeTeamNameEn, f.HomeTeamNameZh);
            AddTeam(teams, seenTeams, f.AwayTeamNameEn, f.AwayTeamNameZh);

            matches.Rows.Add(f.MatchExternalId, (object?)f.SourceGameId ?? DBNull.Value,
                f.HomeTeamNameEn, f.AwayTeamNameEn, f.KickoffUtc,
                f.TournamentExternalId, (object?)f.TournamentNameEn ?? DBNull.Value,
                (object?)f.TournamentNameZh ?? DBNull.Value);

            foreach (var m in f.Markets)
            {
                markets.Rows.Add(m.MarketExternalId, f.MatchExternalId, m.NameEn,
                    (object?)m.NameZh ?? DBNull.Value, (object?)m.MarketTypeCode ?? DBNull.Value,
                    (object?)m.Line ?? DBNull.Value);

                foreach (var s in m.Selections)
                {
                    selections.Rows.Add(s.SelectionExternalId, m.MarketExternalId, s.NameEn,
                        (object?)s.NameZh ?? DBNull.Value, (object?)s.Side ?? DBNull.Value);
                    odds.Rows.Add(s.SelectionExternalId, s.Pd, s.Pu, s.DecimalOdds);
                }
            }
        }

        var p = new DynamicParameters();
        p.Add("@Teams", teams.AsTableValuedParameter("dbo.TeamTvp"));
        p.Add("@Matches", matches.AsTableValuedParameter("dbo.MatchTvp"));
        p.Add("@Markets", markets.AsTableValuedParameter("dbo.MarketTvp"));
        p.Add("@Selections", selections.AsTableValuedParameter("dbo.SelectionTvp"));
        p.Add("@Odds", odds.AsTableValuedParameter("dbo.OddsTvp"));
        return p;
    }

    private static DataTable NewLiveStateTable()
    {
        var t = new DataTable();
        t.Columns.Add("MatchExternalId", typeof(string));
        t.Columns.Add("HomeScore", typeof(int));
        t.Columns.Add("AwayScore", typeof(int));
        t.Columns.Add("DomainStatus", typeof(string));
        t.Columns.Add("LivePhase", typeof(string));
        t.Columns.Add("MatchMinute", typeof(string));
        return t;
    }

    private static void AddTeam(DataTable t, HashSet<string> seen, string en, string? zh)
    {
        if (seen.Add(en)) t.Rows.Add(en, (object?)zh ?? DBNull.Value);
    }

    private static DataTable NewTeamsTable()
    {
        var t = new DataTable();
        t.Columns.Add("NameEn", typeof(string));
        t.Columns.Add("NameZh", typeof(string));
        return t;
    }

    private static DataTable NewMatchesTable()
    {
        var t = new DataTable();
        t.Columns.Add("MatchExternalId", typeof(string));
        t.Columns.Add("SourceGameId", typeof(string));
        t.Columns.Add("HomeTeamNameEn", typeof(string));
        t.Columns.Add("AwayTeamNameEn", typeof(string));
        t.Columns.Add("KickoffUtc", typeof(DateTime));
        t.Columns.Add("TournamentExternalId", typeof(string));
        t.Columns.Add("TournamentNameEn", typeof(string));
        t.Columns.Add("TournamentNameZh", typeof(string));
        return t;
    }

    private static DataTable NewMarketsTable()
    {
        var t = new DataTable();
        t.Columns.Add("MarketExternalId", typeof(string));
        t.Columns.Add("MatchExternalId", typeof(string));
        t.Columns.Add("NameEn", typeof(string));
        t.Columns.Add("NameZh", typeof(string));
        t.Columns.Add("MarketTypeCode", typeof(string));
        t.Columns.Add("Line", typeof(decimal));
        return t;
    }

    private static DataTable NewSelectionsTable()
    {
        var t = new DataTable();
        t.Columns.Add("SelectionExternalId", typeof(string));
        t.Columns.Add("MarketExternalId", typeof(string));
        t.Columns.Add("NameEn", typeof(string));
        t.Columns.Add("NameZh", typeof(string));
        t.Columns.Add("Side", typeof(string));
        return t;
    }

    private static DataTable NewOddsTable()
    {
        var t = new DataTable();
        t.Columns.Add("SelectionExternalId", typeof(string));
        t.Columns.Add("Pd", typeof(decimal));
        t.Columns.Add("Pu", typeof(decimal));
        t.Columns.Add("DecimalOdds", typeof(decimal));
        return t;
    }
}
