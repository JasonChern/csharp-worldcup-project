namespace WorldCup.IngestionJob;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    public string EnUrl { get; set; } = "https://blob3rd.sportslottery.com.tw/apidata/Pre/34740.1-Games.en.json";
    public string ZhUrl { get; set; } = "https://blob3rd.sportslottery.com.tw/apidata/Pre/34740.1-Games.zh.json";
    public string MatchServiceBaseUrl { get; set; } = "http://localhost:5080";
    public string WorldCupTournamentId { get; set; } = "29614";
    public int IntervalSeconds { get; set; } = 300;
}
