using WorldCup.IngestionJob;
using WorldCup.IngestionJob.Fetching;
using WorldCup.IngestionJob.Publishing;

var builder = Host.CreateApplicationBuilder(args);

var opts = builder.Configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>()
           ?? new IngestionOptions();
builder.Services.AddSingleton(opts);

builder.Services.AddHttpClient<ISportsLotteryClient, SportsLotteryClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<IMatchServiceClient, MatchServiceClient>(c =>
{
    c.BaseAddress = new Uri(opts.MatchServiceBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
