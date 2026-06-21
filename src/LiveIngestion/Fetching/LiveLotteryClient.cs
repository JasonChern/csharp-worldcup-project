using System.Text.Json;

namespace WorldCup.LiveIngestion.Fetching;

public interface ILiveLotteryClient
{
    Task<IReadOnlyList<LiveGame>> FetchEnglishAsync(CancellationToken ct);
    Task<IReadOnlyList<LiveGame>> FetchChineseAsync(CancellationToken ct);
}

public sealed class LiveLotteryClient : ILiveLotteryClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly LiveOptions _opts;

    public LiveLotteryClient(HttpClient http, LiveOptions opts)
    {
        _http = http;
        _opts = opts;
    }

    public Task<IReadOnlyList<LiveGame>> FetchEnglishAsync(CancellationToken ct) => FetchAsync(_opts.EnUrl, ct);
    public Task<IReadOnlyList<LiveGame>> FetchChineseAsync(CancellationToken ct) => FetchAsync(_opts.ZhUrl, ct);

    private async Task<IReadOnlyList<LiveGame>> FetchAsync(string url, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync(url, ct);
        var games = await JsonSerializer.DeserializeAsync<List<LiveGame>>(stream, JsonOpts, ct);
        return games ?? new List<LiveGame>();
    }
}
