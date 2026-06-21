namespace WorldCup.MatchService.Domain;

// 純領域實體（無框架相依）。寫入路徑主要透過 SP/TVP，實體用於表達模型與讀取對應。

public class Team
{
    public Guid TeamId { get; set; }
    public string NameEn { get; set; } = "";
    public string? NameZh { get; set; }
    public string? GroupCode { get; set; }
}

public class Match
{
    public Guid MatchId { get; set; }
    public string ExternalId { get; set; } = "";
    public string? SourceGameId { get; set; }
    public Guid HomeTeamId { get; set; }
    public Guid AwayTeamId { get; set; }
    public DateTime KickoffUtc { get; set; }
    public string TournamentExternalId { get; set; } = "";
    public string? TournamentNameEn { get; set; }
    public string? TournamentNameZh { get; set; }
    public MatchStatus Status { get; set; } = MatchStatus.Scheduled;
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
}

public enum MatchStatus { Scheduled, Live, Ended }

public class Market
{
    public long MarketId { get; set; }
    public string ExternalId { get; set; } = "";
    public Guid MatchId { get; set; }
    public string NameEn { get; set; } = "";
    public string? NameZh { get; set; }
    public string? MarketTypeCode { get; set; }
    public decimal? Line { get; set; }
}

public class MarketSelection
{
    public long SelectionId { get; set; }
    public string ExternalId { get; set; } = "";
    public long MarketId { get; set; }
    public string NameEn { get; set; } = "";
    public string? NameZh { get; set; }
    public string? Side { get; set; }
}

public class OddsSnapshot
{
    public long SnapshotId { get; set; }
    public long SelectionId { get; set; }
    public decimal Pd { get; set; }
    public decimal Pu { get; set; }
    public decimal DecimalOdds { get; set; }
    public DateTime FetchedUtc { get; set; }
}
