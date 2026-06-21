using System.Text.Json;

namespace WorldCup.IngestionJob.Fetching;

public interface ISportsLotteryClient
{
    Task<IReadOnlyList<SlGame>> FetchEnglishAsync(CancellationToken ct);
    Task<IReadOnlyList<SlGame>> FetchChineseAsync(CancellationToken ct);
}

/// <summary>抓取運彩賽前 JSON（en / zh 兩版）。URL 由設定提供。</summary>
public sealed class SportsLotteryClient : ISportsLotteryClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly IngestionOptions _opts;

    public SportsLotteryClient(HttpClient http, IngestionOptions opts)
    {
        _http = http;
        _opts = opts;
    }

    public Task<IReadOnlyList<SlGame>> FetchEnglishAsync(CancellationToken ct) => FetchAsync(_opts.EnUrl, ct);
    public Task<IReadOnlyList<SlGame>> FetchChineseAsync(CancellationToken ct) => FetchAsync(_opts.ZhUrl, ct);

    private async Task<IReadOnlyList<SlGame>> FetchAsync(string url, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync(url, ct);
        var games = await JsonSerializer.DeserializeAsync<List<SlGame>>(stream, JsonOpts, ct);
        return games ?? new List<SlGame>();
    }
}
