using System.Data;
using System.Text.Json;
using Dapper;
using MassTransit;
using Microsoft.Data.SqlClient;
using WorldCup.Contracts.Events;

namespace WorldCup.OutboxPublisher;

/// <summary>輪詢 MatchDb 的 OutboxMessages，未發送的以 MassTransit 發布到 RabbitMQ，成功後標記已發送（at-least-once）。</summary>
public sealed class OutboxPublisherWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly OutboxOptions _opts;
    private readonly IBus _bus;
    private readonly ILogger<OutboxPublisherWorker> _logger;

    public OutboxPublisherWorker(OutboxOptions opts, IBus bus, ILogger<OutboxPublisherWorker> logger)
    {
        _opts = opts;
        _bus = bus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForDbAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _opts.IntervalSeconds)));
        do
        {
            try
            {
                var published = await DispatchBatchAsync(stoppingToken);
                if (published > 0)
                    _logger.LogInformation("已發布 {Count} 則 Outbox 事件。", published);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox 發布循環錯誤，下次重試。");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_opts.ConnectionString);
        var rows = (await conn.QueryAsync<OutboxRow>(new CommandDefinition(
            "dbo.sp_GetUnprocessedOutbox", new { Take = _opts.BatchSize },
            commandType: CommandType.StoredProcedure, cancellationToken: ct))).ToList();

        var count = 0;
        foreach (var row in rows)
        {
            if (!await PublishAsync(row, ct)) continue;
            await conn.ExecuteAsync(new CommandDefinition(
                "dbo.sp_MarkOutboxProcessed", new { row.OutboxId },
                commandType: CommandType.StoredProcedure, cancellationToken: ct));
            count++;
        }
        return count;
    }

    private async Task<bool> PublishAsync(OutboxRow row, CancellationToken ct)
    {
        switch (row.EventType)
        {
            case "OddsChanged":
                var evt = JsonSerializer.Deserialize<OddsChanged>(row.Payload, JsonOpts);
                if (evt is null)
                {
                    _logger.LogWarning("Outbox #{Id} 反序列化為 null，跳過。", row.OutboxId);
                    return false;
                }
                await _bus.Publish(evt, ct);
                return true;
            default:
                _logger.LogWarning("未知事件型別 {Type}（Outbox #{Id}），跳過。", row.EventType, row.OutboxId);
                return false;
        }
    }

    private async Task WaitForDbAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_opts.ConnectionString);
                await conn.OpenAsync(ct);
                await conn.ExecuteAsync("SELECT 1");
                return;
            }
            catch (SqlException) when (attempt < 30)
            {
                _logger.LogInformation("等待 MatchDb 就緒…（{Attempt}/30）", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private sealed class OutboxRow
    {
        public long OutboxId { get; set; }
        public string EventType { get; set; } = "";
        public string Payload { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
    }
}
