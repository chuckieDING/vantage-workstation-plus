using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VantageWorkstationPlus.Services
{
    /// <summary>简易调度器：支持 "Nm"（分钟）/ "Nh"（小时）间隔；MVP 不接 Quartz，等真实有 6+ jobs 再换。
    /// 每个 DbDataSource 一个 DispatcherTimer，触发时跑 DbExtractor.Extract → WriteToInbox → 触发 NewInboxFile 事件。</summary>
    public class DbScheduler : IDisposable
    {
        private readonly Dictionary<string, DispatcherTimer> _timers = new();

        /// <summary>新 inbox 文件产生时触发：(数据源名, 落地的 xlsx 路径)。</summary>
        public event Action<string, string>? NewInboxFile;
        public event Action<string, Exception>? ExtractFailed;

        public void Schedule(IEnumerable<DbDataSource> sources)
        {
            Stop();
            foreach (var ds in sources.Where(s => !string.IsNullOrEmpty(s.CronOrInterval)))
            {
                if (!TryParseInterval(ds.CronOrInterval!, out var interval)) continue;
                var t = new DispatcherTimer { Interval = interval, Tag = ds };
                t.Tick += (_, _) => _ = RunOnce(ds);
                t.Start();
                _timers[ds.Name] = t;
                AppLog.Info($"DbScheduler: {ds.Name} 已调度，每 {interval}");
            }
        }

        public async Task<string?> RunOnce(DbDataSource ds)
        {
            try
            {
                var (ids, details) = await Task.Run(() => DbExtractor.Extract(ds));
                if (ids.Count == 0)
                {
                    AppLog.Info($"DbScheduler: {ds.Name} 无新数据");
                    return null;
                }
                string file = DbExtractor.WriteToInbox(ds, details);
                ds.LastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                AppLog.Info($"DbScheduler: {ds.Name} 抽到 {ids.Count} 条 → {file}");
                NewInboxFile?.Invoke(ds.Name, file);
                return file;
            }
            catch (Exception ex)
            {
                AppLog.Error($"DbScheduler.{ds.Name}", ex);
                ExtractFailed?.Invoke(ds.Name, ex);
                return null;
            }
        }

        public void Stop()
        {
            foreach (var t in _timers.Values) t.Stop();
            _timers.Clear();
        }

        public void Dispose() => Stop();

        /// <summary>支持 "30m"/"2h"/"5s" 三种简单间隔；将来要 cron 表达式再换 Cronos/Quartz。</summary>
        public static bool TryParseInterval(string s, out TimeSpan ts)
        {
            ts = TimeSpan.Zero;
            var m = Regex.Match(s.Trim(), @"^(\d+)\s*([smh])$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            int n = int.Parse(m.Groups[1].Value);
            ts = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                "h" => TimeSpan.FromHours(n),
                _ => TimeSpan.Zero,
            };
            return ts > TimeSpan.Zero;
        }
    }
}
