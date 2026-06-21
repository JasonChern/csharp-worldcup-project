using Microsoft.AspNetCore.SignalR;

namespace WorldCup.RealtimeService.Hubs;

/// <summary>前端連這個 Hub 收即時更新。事件由 MassTransit 消費者經 IHubContext 推播。
/// 推播方法名：ScoreUpdated / StatusUpdated。</summary>
public sealed class LiveHub : Hub
{
}
