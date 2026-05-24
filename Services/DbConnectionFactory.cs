using System;
using System.Data;
using System.Data.Common;

namespace VantageWorkstationPlus.Services
{
    public enum DbProvider { SqlServer, MySql, Postgres, Sqlite, Oracle }

    /// <summary>按 provider + 连接串造 DbConnection（已 Open）。
    /// 5 个 driver 都通过 System.Data.Common 抽象，调用方用 Dapper 操作即可，零 driver-specific 代码。</summary>
    public static class DbConnectionFactory
    {
        public static DbConnection Open(DbProvider provider, string connectionString)
        {
            DbConnection conn = provider switch
            {
                DbProvider.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(connectionString),
                DbProvider.MySql => new MySqlConnector.MySqlConnection(connectionString),
                DbProvider.Postgres => new Npgsql.NpgsqlConnection(connectionString),
                DbProvider.Sqlite => new Microsoft.Data.Sqlite.SqliteConnection(connectionString),
                DbProvider.Oracle => new Oracle.ManagedDataAccess.Client.OracleConnection(connectionString),
                _ => throw new NotSupportedException($"未知 DbProvider: {provider}"),
            };
            conn.Open();
            return conn;
        }

        /// <summary>不抛异常的连接测试；返回 (成功, 错误信息)。</summary>
        public static (bool Ok, string? Error) TestConnection(DbProvider provider, string connectionString)
        {
            try
            {
                using var c = Open(provider, connectionString);
                using var cmd = c.CreateCommand();
                // Oracle 的 SELECT 必须带 FROM；其他 4 个都能裸 SELECT 1
                cmd.CommandText = provider == DbProvider.Oracle ? "SELECT 1 FROM DUAL" : "SELECT 1";
                cmd.CommandTimeout = 10;
                cmd.ExecuteScalar();
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>给 UI 显示用的连接串示例，每个 provider 一个。</summary>
        public static string ExampleConnectionString(DbProvider provider) => provider switch
        {
            DbProvider.SqlServer => "Server=host,1433;Database=db;User Id=u;Password=DPAPI:;TrustServerCertificate=true",
            DbProvider.MySql     => "Server=host;Port=3306;Database=db;User Id=u;Password=DPAPI:;SslMode=Preferred",
            DbProvider.Postgres  => "Host=host;Port=5432;Database=db;Username=u;Password=DPAPI:;SSL Mode=Prefer",
            DbProvider.Sqlite    => "Data Source=C:\\path\\to\\file.db",
            DbProvider.Oracle    => "User Id=u;Password=DPAPI:;Data Source=//host:1521/SERVICE_NAME",
            _ => "",
        };
    }
}
