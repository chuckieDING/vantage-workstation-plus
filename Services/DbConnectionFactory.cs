using System;
using System.Data;
using System.Data.Common;

namespace VantageWorkstationPlus.Services
{
    public enum DbProvider { SqlServer, MySql, Postgres }

    /// <summary>按 provider + 连接串造 DbConnection（已 Open）。
    /// 4 个 driver 都通过 System.Data.Common 抽象，调用方用 Dapper 操作即可，零 driver-specific 代码。</summary>
    public static class DbConnectionFactory
    {
        public static DbConnection Open(DbProvider provider, string connectionString)
        {
            DbConnection conn = provider switch
            {
                DbProvider.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(connectionString),
                DbProvider.MySql => new MySqlConnector.MySqlConnection(connectionString),
                DbProvider.Postgres => new Npgsql.NpgsqlConnection(connectionString),
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
                cmd.CommandText = provider switch
                {
                    DbProvider.SqlServer => "SELECT 1",
                    DbProvider.MySql => "SELECT 1",
                    DbProvider.Postgres => "SELECT 1",
                    _ => "SELECT 1",
                };
                cmd.CommandTimeout = 10;
                cmd.ExecuteScalar();
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
