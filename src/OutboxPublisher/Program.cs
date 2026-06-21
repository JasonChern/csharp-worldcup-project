using MassTransit;
using WorldCup.OutboxPublisher;

var builder = Host.CreateApplicationBuilder(args);

var opts = new OutboxOptions
{
    ConnectionString = builder.Configuration.GetConnectionString("MatchDb")
        ?? throw new InvalidOperationException("缺少 ConnectionStrings:MatchDb"),
    RabbitHost = builder.Configuration["RabbitMq:Host"] ?? "localhost",
    RabbitUser = builder.Configuration["RabbitMq:Username"] ?? "guest",
    RabbitPass = builder.Configuration["RabbitMq:Password"] ?? "guest",
    IntervalSeconds = int.TryParse(builder.Configuration["Outbox:IntervalSeconds"], out var i) ? i : 5,
    BatchSize = int.TryParse(builder.Configuration["Outbox:BatchSize"], out var b) ? b : 100,
};
builder.Services.AddSingleton(opts);

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((_, cfg) =>
    {
        cfg.Host(opts.RabbitHost, "/", h =>
        {
            h.Username(opts.RabbitUser);
            h.Password(opts.RabbitPass);
        });
    });
});

builder.Services.AddHostedService<OutboxPublisherWorker>();

builder.Build().Run();
