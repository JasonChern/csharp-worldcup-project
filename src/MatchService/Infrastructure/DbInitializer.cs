using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using WorldCup.MatchService.Application;

namespace WorldCup.MatchService.Infrastructure;

/// <summary>啟動時依序執行 db/01_schema、02_types、03_procedures（以 GO 分批）。冪等。</summary>
public sealed class DbInitializer : IDbInitializer
{
    private static readonly string[] ScriptOrder =
        { "01_schema.sql", "02_types.sql", "03_procedures.sql" };

    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<DbInitializer> _logger;
    private readonly string _scriptsPath;

    public DbInitializer(ISqlConnectionFactory factory, ILogger<DbInitializer> logger, string scriptsPath)
    {
        _factory = factory;
        _logger = logger;
        _scriptsPath = ResolveScriptsPath(scriptsPath);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _logger.LogInformation("DB 初始化，腳本路徑：{Path}", _scriptsPath);
        await WaitForServerAsync(ct);

        foreach (var file in ScriptOrder)
        {
            var full = Path.Combine(_scriptsPath, file);
            if (!File.Exists(full))
                throw new FileNotFoundException($"找不到 DB 腳本：{full}");

            var sql = await File.ReadAllTextAsync(full, ct);
            // master 連線執行（01 含 CREATE DATABASE / USE MatchDb）
            await using var conn = _factory.CreateMaster();
            await conn.OpenAsync(ct);
            foreach (var batch in SplitOnGo(sql))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                await conn.ExecuteAsync(batch);
            }
            _logger.LogInformation("已套用腳本 {File}", file);
        }
        _logger.LogInformation("DB 初始化完成。");
    }

    private async Task WaitForServerAsync(CancellationToken ct)
    {
        // SQL Server 容器啟動較慢，重試連線。
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                await using var conn = _factory.CreateMaster();
                await conn.OpenAsync(ct);
                await conn.ExecuteAsync("SELECT 1");
                return;
            }
            catch (SqlException) when (attempt < 30)
            {
                _logger.LogInformation("等待 SQL Server 就緒…（{Attempt}/30）", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static IEnumerable<string> SplitOnGo(string sql)
    {
        var sb = new StringBuilder();
        foreach (var line in sql.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else
            {
                sb.Append(line).Append('\n');
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string ResolveScriptsPath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);

        // 從 BaseDirectory 往上找 db 資料夾（本機開發用）
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "db");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? "db" : configured);
    }
}
