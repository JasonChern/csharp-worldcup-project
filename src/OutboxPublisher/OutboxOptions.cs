namespace WorldCup.OutboxPublisher;

public sealed class OutboxOptions
{
    public string ConnectionString { get; set; } = "";
    public string RabbitHost { get; set; } = "localhost";
    public string RabbitUser { get; set; } = "guest";
    public string RabbitPass { get; set; } = "guest";
    public int IntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 100;
}
