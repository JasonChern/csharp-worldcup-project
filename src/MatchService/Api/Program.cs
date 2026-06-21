using WorldCup.Contracts;
using WorldCup.MatchService.Application;
using WorldCup.MatchService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<FixtureIngestionService>();
builder.Services.AddEndpointsApiExplorer();

var corsOrigins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:8088,http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

// 啟動時初始化 DB（schema/types/procedures，冪等）
using (var scope = app.Services.CreateScope())
{
    var init = scope.ServiceProvider.GetRequiredService<IDbInitializer>();
    await init.InitializeAsync(CancellationToken.None);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Ingestion → 寫入一批賽事/玩法/賠率
app.MapPost("/api/fixtures/upsert", async (
    UpsertFixturesRequest request, FixtureIngestionService svc, CancellationToken ct) =>
{
    var result = await svc.IngestAsync(request, ct);
    return Results.Ok(result);
});

// Live 擷取 → 更新比分/狀態 + in-play 賠率
app.MapPost("/api/live/upsert", async (
    UpsertLiveRequest request, FixtureIngestionService svc, CancellationToken ct) =>
{
    var result = await svc.IngestLiveAsync(request, ct);
    return Results.Ok(result);
});

// 賽程列表（可選 tournament 過濾，世足為 29614）
app.MapGet("/api/matches", async (
    string? tournament, IMatchReadRepository repo, CancellationToken ct) =>
{
    var matches = await repo.GetMatchesAsync(tournament, ct);
    return Results.Ok(matches);
});

// 某場比賽的玩法 + 各選項最新賠率
app.MapGet("/api/match-markets", async (
    string match, IMatchReadRepository repo, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(match))
        return Results.BadRequest(new { error = "缺少 match（MatchExternalId）" });
    var markets = await repo.GetMatchMarketsAsync(match, ct);
    return Results.Ok(markets);
});

app.Run();
