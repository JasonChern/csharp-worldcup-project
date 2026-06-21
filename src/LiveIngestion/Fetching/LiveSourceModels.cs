using System.Text.Json.Serialization;

namespace WorldCup.LiveIngestion.Fetching;

// Live feed 與賽前同結構，多 lds/ldss。ldss 是「字串化 JSON」，需二次解析。

public sealed class LiveGame
{
    [JsonPropertyName("id")]  public string Id { get; set; } = "";
    [JsonPropertyName("er")]  public string? ExternalRef { get; set; }
    [JsonPropertyName("hn")]  public string HomeName { get; set; } = "";
    [JsonPropertyName("an")]  public string AwayName { get; set; } = "";
    [JsonPropertyName("kt")]  public DateTimeOffset Kickoff { get; set; }
    [JsonPropertyName("ti")]  public string TournamentId { get; set; } = "";
    [JsonPropertyName("tn")]  public string? TournamentName { get; set; }
    [JsonPropertyName("ldss")] public string? LiveStateJson { get; set; }   // stringified JSON
    [JsonPropertyName("ms")]  public List<LiveMarket> Markets { get; set; } = new();
}

public sealed class LiveMarket
{
    [JsonPropertyName("id")]   public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ti")]   public string? MarketTypeCode { get; set; }
    [JsonPropertyName("mv")]   public decimal? Line { get; set; }
    [JsonPropertyName("cs")]   public List<LiveChoice> Choices { get; set; } = new();
}

public sealed class LiveChoice
{
    [JsonPropertyName("id")]   public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("v")]    public string? Side { get; set; }
    [JsonPropertyName("pd")]   public string? Pd { get; set; }
    [JsonPropertyName("pu")]   public string? Pu { get; set; }
}

// ldss 內層
public sealed class LdssState
{
    [JsonPropertyName("id")]        public string? Id { get; set; }          // sr:match:...
    [JsonPropertyName("matchTime")] public string? MatchTime { get; set; }
    [JsonPropertyName("status")]    public string? Status { get; set; }      // 1h/ht/2h/ended...
    [JsonPropertyName("scores")]    public LdssScores? Scores { get; set; }
}

public sealed class LdssScores
{
    [JsonPropertyName("CURRENT_SCORE")] public LdssScore? Current { get; set; }
}

public sealed class LdssScore
{
    [JsonPropertyName("home")] public int Home { get; set; }
    [JsonPropertyName("away")] public int Away { get; set; }
}
