using System.Text.Json.Serialization;

namespace WorldCup.IngestionJob.Fetching;

// 對齊台灣運彩 JSON（欄位短碼）。pd/pu 在來源是字串，故以 string 接、於 Mapper 轉 decimal。

public sealed class SlGame
{
    [JsonPropertyName("id")]  public string Id { get; set; } = "";
    [JsonPropertyName("er")]  public string? ExternalRef { get; set; }   // sr:match:xxxx
    [JsonPropertyName("hn")]  public string HomeName { get; set; } = ""; // 主
    [JsonPropertyName("an")]  public string AwayName { get; set; } = ""; // 客
    [JsonPropertyName("kt")]  public DateTimeOffset Kickoff { get; set; }
    [JsonPropertyName("ti")]  public string TournamentId { get; set; } = "";
    [JsonPropertyName("tn")]  public string? TournamentName { get; set; }
    [JsonPropertyName("san")] public string? Sport { get; set; }
    [JsonPropertyName("ms")]  public List<SlMarket> Markets { get; set; } = new();
}

public sealed class SlMarket
{
    [JsonPropertyName("id")]   public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ti")]   public string? MarketTypeCode { get; set; }
    [JsonPropertyName("mv")]   public decimal? Line { get; set; }
    [JsonPropertyName("cs")]   public List<SlChoice> Choices { get; set; } = new();
}

public sealed class SlChoice
{
    [JsonPropertyName("id")]   public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("v")]    public string? Side { get; set; }   // H/A/D
    [JsonPropertyName("pd")]   public string? Pd { get; set; }     // 來源為字串
    [JsonPropertyName("pu")]   public string? Pu { get; set; }
}
