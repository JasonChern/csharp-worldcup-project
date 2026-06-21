namespace WorldCup.LiveIngestion;

public sealed class LiveOptions
{
    public const string SectionName = "Live";

    public string EnUrl { get; set; } = "https://blob3rd.sportslottery.com.tw/apidata/Live/Games.en.json";
    public string ZhUrl { get; set; } = "https://blob3rd.sportslottery.com.tw/apidata/Live/Games.zh.json";
    public string MatchServiceBaseUrl { get; set; } = "http://localhost:5080";
    public string WorldCupTournamentId { get; set; } = "29614";
    public int IntervalSeconds { get; set; } = 12;
}
