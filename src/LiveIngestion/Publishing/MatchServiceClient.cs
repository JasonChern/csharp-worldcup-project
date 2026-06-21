using System.Net.Http.Json;
using WorldCup.Contracts;

namespace WorldCup.LiveIngestion.Publishing;

public interface IMatchServiceClient
{
    Task<LiveIngestResult?> UpsertLiveAsync(UpsertLiveRequest request, CancellationToken ct);
}

public sealed class MatchServiceClient : IMatchServiceClient
{
    private readonly HttpClient _http;

    public MatchServiceClient(HttpClient http) => _http = http;

    public async Task<LiveIngestResult?> UpsertLiveAsync(UpsertLiveRequest request, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("/api/live/upsert", request, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<LiveIngestResult>(cancellationToken: ct);
    }
}
