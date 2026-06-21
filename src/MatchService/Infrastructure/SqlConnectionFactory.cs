using Microsoft.Data.SqlClient;

namespace WorldCup.MatchService.Infrastructure;

public interface ISqlConnectionFactory
{
    SqlConnection Create();                 // 連到 MatchDb
    SqlConnection CreateMaster();           // 連到 master（給建庫用）
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString) => _connectionString = connectionString;

    public SqlConnection Create() => new(_connectionString);

    public SqlConnection CreateMaster()
    {
        var b = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        return new SqlConnection(b.ConnectionString);
    }
}
