using MassTransit;
using WorldCup.RealtimeService.Consumers;
using WorldCup.RealtimeService.Hubs;

var builder = WebApplication.CreateBuilder(args);

var rabbitHost = builder.Configuration["RabbitMq:Host"] ?? "localhost";
var rabbitUser = builder.Configuration["RabbitMq:Username"] ?? "guest";
var rabbitPass = builder.Configuration["RabbitMq:Password"] ?? "guest";
var corsOrigins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:8088,http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddSignalR();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));   // SignalR 需要

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ScoreBroadcastConsumer>();
    x.AddConsumer<StatusBroadcastConsumer>();
    x.AddConsumer<OddsBroadcastConsumer>();
    x.AddConsumer<ClockBroadcastConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(rabbitHost, "/", h => { h.Username(rabbitUser); h.Password(rabbitPass); });
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapHub<LiveHub>("/hubs/live");

app.Run();
