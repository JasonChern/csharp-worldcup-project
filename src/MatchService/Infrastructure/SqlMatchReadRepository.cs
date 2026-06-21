using System.Data;
using Dapper;
using WorldCup.MatchService.Application;

namespace WorldCup.MatchService.Infrastructure;

public sealed class SqlMatchReadRepository : IMatchReadRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlMatchReadRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<MatchSummary>> GetMatchesAsync(string? tournamentExternalId, CancellationToken ct)
    {
        await using var conn = _factory.Create();
        var cmd = new CommandDefinition("dbo.sp_GetMatches",
            new { TournamentExternalId = tournamentExternalId },
            commandType: CommandType.StoredProcedure, cancellationToken: ct);
        var rows = await conn.QueryAsync<MatchSummary>(cmd);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<MarketWithOdds>> GetMatchMarketsAsync(string matchExternalId, CancellationToken ct)
    {
        await using var conn = _factory.Create();
        var cmd = new CommandDefinition("dbo.sp_GetMatchMarkets",
            new { MatchExternalId = matchExternalId },
            commandType: CommandType.StoredProcedure, cancellationToken: ct);
        var rows = await conn.QueryAsync<MarketWithOdds>(cmd);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<OddsPoint>> GetOddsHistoryAsync(string selectionExternalId, CancellationToken ct)
    {
        await using var conn = _factory.Create();
        var cmd = new CommandDefinition("dbo.sp_GetOddsHistory",
            new { SelectionExternalId = selectionExternalId },
            commandType: CommandType.StoredProcedure, cancellationToken: ct);
        var rows = await conn.QueryAsync<OddsPoint>(cmd);
        return rows.ToList();
    }
}
