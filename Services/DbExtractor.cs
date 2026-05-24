using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;

namespace VantageWorkstationPlus.Services
{
    /// <summary>从数据源按配置 SQL 抽取数据，落地到 ./inbox/{name}-{ts}.xlsx 或返回内存列表。
    /// 字段映射：SQL 结果至少含一列作为 ID（IdColumn 配置），其他列做辅助信息保留。</summary>
    public class DbDataSource
    {
        public string Name { get; set; } = "";              // 例 "DB-SignOff-Daily"
        public DbProvider Provider { get; set; }
        public string ConnectionString { get; set; } = "";  // 含 DPAPI: 前缀的密文段
        public string Query { get; set; } = "";             // 完整 SQL，可含 @date / @user 等参数
        public string IdColumn { get; set; } = "id";        // 哪一列作为 ID（首列后备）
        public List<string> AuxColumns { get; set; } = new(); // 显示用辅助列
        public string TargetPanel { get; set; } = "";       // signoff/dehydration/archive/transfer
        public string? CronOrInterval { get; set; }         // null=手动；"*/30 * * * *" 或 "30m"
        public string LastRun { get; set; } = "";
    }

    public static class DbExtractor
    {
        /// <summary>跑一次抽取，返回 (Id, 辅助字典) 列表。SQL 参数支持 {date}/{user}/{today} 占位符。</summary>
        public static (List<string> Ids, List<Dictionary<string, object?>> Details) Extract(
            DbDataSource ds, Dictionary<string, object>? sqlParams = null)
        {
            string connStr = ResolveConnectionString(ds.ConnectionString);
            using var conn = DbConnectionFactory.Open(ds.Provider, connStr);
            string sql = SubstitutePlaceholders(ds.Query);
            var rows = conn.Query(sql, sqlParams).Cast<IDictionary<string, object?>>().ToList();
            var ids = new List<string>();
            var details = new List<Dictionary<string, object?>>();
            foreach (var row in rows)
            {
                var dict = row.ToDictionary(kv => kv.Key, kv => kv.Value);
                string id = dict.TryGetValue(ds.IdColumn, out var v) && v != null
                    ? v.ToString() ?? ""
                    : (dict.Values.FirstOrDefault()?.ToString() ?? "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                ids.Add(id);
                details.Add(dict);
            }
            return (ids, details);
        }

        /// <summary>写抽取结果到 ./inbox/{name}-{timestamp}.csv（UTF-8 BOM），列 = IdColumn + AuxColumns。
        /// CSV 而非 xlsx 避免跨 PR 依赖（SlideListReader 也能直接读 CSV）。</summary>
        public static string WriteToInbox(DbDataSource ds,
            List<Dictionary<string, object?>> details)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "inbox");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{ds.TargetPanel}-{ds.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            var headers = new List<string> { ds.IdColumn };
            headers.AddRange(ds.AuxColumns);
            using var sw = new StreamWriter(file, false, new System.Text.UTF8Encoding(true));
            sw.WriteLine(string.Join(",", headers.Select(EscapeCsv)));
            foreach (var d in details)
            {
                sw.WriteLine(string.Join(",", headers.Select(h =>
                    EscapeCsv(d.TryGetValue(h, out var v) && v != null ? v.ToString() ?? "" : ""))));
            }
            return file;
        }

        private static string EscapeCsv(string? s)
        {
            s ??= "";
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string ResolveConnectionString(string conn)
        {
            // 支持 "Server=...;Password=DPAPI:xxx;..." 形式：仅 Password 字段解密
            if (string.IsNullOrEmpty(conn)) return conn;
            var parts = conn.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq < 0) continue;
                string val = parts[i].Substring(eq + 1).Trim();
                if (val.StartsWith("DPAPI:", StringComparison.Ordinal))
                    parts[i] = parts[i].Substring(0, eq + 1) + SecretProtector.Decrypt(val);
            }
            return string.Join(";", parts);
        }

        /// <summary>主程序登录后可设置：() =&gt; App.SoapSession?.LoggedInUser?.UserName。
        /// 未设置时回退到 Environment.UserName，方便 ConfigTool 等无登录态的进程复用。</summary>
        public static Func<string>? CurrentUserProvider { get; set; }

        private static string SubstitutePlaceholders(string sql) => sql
            .Replace("{date}", DateTime.Today.ToString("yyyy-MM-dd"))
            .Replace("{today}", DateTime.Today.ToString("yyyy-MM-dd"))
            .Replace("{now}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Replace("{user}", CurrentUserProvider?.Invoke() ?? Environment.UserName);
    }
}
