using MassTransit;
using WorldCup.NotificationService;

var builder = Host.CreateApplicationBuilder(args);

var host = builder.Configuration["RabbitMq:Host"] ?? "localhost";
var user = builder.Configuration["RabbitMq:Username"] ?? "guest";
var pass = builder.Configuration["RabbitMq:Password"] ?? "guest";

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OddsChangedConsumer>();
    x.AddConsumer<MatchScoreChangedConsumer>();
    x.AddConsumer<MatchStatusChangedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(host, "/", h =>
        {
            h.Username(user);
            h.Password(pass);
        });
        cfg.ConfigureEndpoints(ctx);
    });
});

builder.Build().Run();
