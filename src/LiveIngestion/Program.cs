using MassTransit;
using WorldCup.LiveIngestion;
using WorldCup.LiveIngestion.Fetching;
using WorldCup.LiveIngestion.Publishing;

var builder = Host.CreateApplicationBuilder(args);

var opts = builder.Configuration.GetSection(LiveOptions.SectionName).Get<LiveOptions>()
           ?? new LiveOptions();
builder.Services.AddSingleton(opts);

// 直接發布 MatchClock 到 RabbitMQ（時鐘 tick，不需 Outbox 持久化）
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((_, cfg) =>
    {
        cfg.Host(opts.RabbitHost, "/", h => { h.Username(opts.RabbitUser); h.Password(opts.RabbitPass); });
    });
});

builder.Services.AddHttpClient<ILiveLotteryClient, LiveLotteryClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient<IMatchServiceClient, MatchServiceClient>(c =>
{
    c.BaseAddress = new Uri(opts.MatchServiceBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHostedService<Worker>();

builder.Build().Run();
