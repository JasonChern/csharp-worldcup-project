using WorldCup.LiveIngestion;
using WorldCup.LiveIngestion.Fetching;
using WorldCup.LiveIngestion.Publishing;

var builder = Host.CreateApplicationBuilder(args);

var opts = builder.Configuration.GetSection(LiveOptions.SectionName).Get<LiveOptions>()
           ?? new LiveOptions();
builder.Services.AddSingleton(opts);

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
