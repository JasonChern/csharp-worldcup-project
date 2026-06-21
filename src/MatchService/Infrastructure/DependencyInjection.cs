using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorldCup.MatchService.Application;

namespace WorldCup.MatchService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("MatchDb")
            ?? throw new InvalidOperationException("缺少連線字串 ConnectionStrings:MatchDb");
        var scriptsPath = config["Database:ScriptsPath"] ?? "db";

        services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));
        services.AddScoped<IFixtureRepository, SqlFixtureRepository>();
        services.AddScoped<IMatchReadRepository, SqlMatchReadRepository>();
        services.AddSingleton<IDbInitializer>(sp => new DbInitializer(
            sp.GetRequiredService<ISqlConnectionFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DbInitializer>>(),
            scriptsPath));
        return services;
    }
}
