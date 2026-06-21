using System.Net.Http.Json;
using WorldCup.Contracts;

namespace WorldCup.IngestionJob.Publishing;

public interface IMatchServiceClient
{
    Task<IngestResult?> UpsertFixturesAsync(UpsertFixturesRequest request, CancellationToken ct);
}

/// <summary>呼叫 Match Service 的 POST /api/fixtures/upsert。</summary>
public sealed class MatchServiceClient : IMatchServiceClient
{
    private readonly HttpClient _http;

    public MatchServiceClient(HttpClient http) => _http = http;

    public async Task<IngestResult?> UpsertFixturesAsync(UpsertFixturesRequest request, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("/api/fixtures/upsert", request, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<IngestResult>(cancellationToken: ct);
    }
}
