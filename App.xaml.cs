using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using VantageWorkstationPlus.Services;

namespace VantageWorkstationPlus
{
    public partial class App : Application
    {
        public static AppSession? Session { get; set; }
        public static TouchScreenSession? SoapSession { get; set; }

        // 工作站默认值（从 appsettings.json 加载，可被覆盖）
        public static int WorkCellId { get; set; } = 0;
        public static int EmpUserId { get; set; } = 1;
        public static string WorkCellType { get; set; } = "TissueProcessing";
        public static string ClientVersion { get; set; } = "4.1.25136.1";

        /// <summary>登录后允许显示的 Tab 集合（小写）；空 / null 表示全部。</summary>
        public static HashSet<string>? EnabledTabs { get; set; }

        /// <summary>true 表示接受任意 TLS 证书（自签 / 内网常用）；false 启用严格校验。</summary>
        public static bool AcceptAnyServerCert { get; set; } = true;

        /// <summary>服务器地址（含 http:// 前缀）。从 appsettings 读，登录页可改但不持久化。</summary>
        public static string BaseUrl { get; set; } = "http://192.168.127.128";

        /// <summary>从 appsettings.json 加载的数据源配置（PR3 DB 抽取用）。</summary>
        public static List<Services.DbDataSource> DataSources { get; set; } = new();

        /// <summary>调度器实例，启动时初始化，关闭时 Dispose。</summary>
        public static Services.DbScheduler? Scheduler { get; set; }

        public App()
        {
            // 进程级异常都落到 ./logs/yyyy-MM-dd.log
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                AppLog.Error("UnhandledException", ex ?? new Exception("未知"));
                MessageBox.Show("程序崩溃: " + (ex?.Message ?? "未知错误") +
                    "\n\n详细日志: " + AppLog.LogPath,
                    "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            DispatcherUnhandledException += (_, e) =>
            {
                AppLog.Error("DispatcherUnhandledException", e.Exception);
                MessageBox.Show("程序异常: " + e.Exception.Message +
                    "\n\n详细日志: " + AppLog.LogPath,
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLog.Error("UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            AppLog.Info("=== Application Startup ===");
            AppLog.Info($"BaseDirectory: {AppContext.BaseDirectory}");
            AppLog.Info($"OS: {Environment.OSVersion}, .NET: {Environment.Version}");

            // 让 DbExtractor 的 {user} 占位符用我们登录的用户
            DbExtractor.CurrentUserProvider = () =>
                SoapSession?.LoggedInUser?.UserName ?? Session?.Username ?? "";

            try { LoadSettings(); }
            catch (Exception ex)
            {
                AppLog.Error("LoadSettings", ex);
                MessageBox.Show("appsettings.json 加载失败，使用默认值: " + ex.Message,
                    "配置警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void LoadSettings()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                AppLog.Warn($"appsettings.json 不存在: {path}");
                return;
            }
            var jo = JObject.Parse(File.ReadAllText(path));
            WorkCellId = jo.Value<int?>("WorkCellId") ?? WorkCellId;
            EmpUserId = jo.Value<int?>("EmpUserId") ?? EmpUserId;
            WorkCellType = jo.Value<string>("WorkCellType") ?? WorkCellType;
            ClientVersion = jo.Value<string>("ClientVersion") ?? ClientVersion;

            AcceptAnyServerCert = jo.Value<bool?>("AcceptAnyServerCert") ?? AcceptAnyServerCert;
            BaseUrl = jo.Value<string>("BaseUrl") ?? BaseUrl;

            // DataSources（PR3）
            if (jo["DataSources"] is JArray dsArr)
            {
                foreach (var dsTok in dsArr)
                {
                    try
                    {
                        var ds = dsTok.ToObject<Services.DbDataSource>();
                        if (ds != null) DataSources.Add(ds);
                    }
                    catch (Exception ex) { AppLog.Warn("DataSource 解析失败: " + ex.Message); }
                }
                AppLog.Info($"已加载 {DataSources.Count} 个数据源");
            }

            if (jo["EnabledTabs"] is JArray tabs)
            {
                var set = tabs.Select(t => t.ToString().Trim().ToLowerInvariant())
                              .Where(s => !string.IsNullOrEmpty(s))
                              .ToHashSet();
                if (set.Count > 0) EnabledTabs = set;
            }

            AppLog.Info($"Loaded settings: WorkCellId={WorkCellId}, EmpUserId={EmpUserId}, " +
                $"WorkCellType={WorkCellType}, ClientVersion={ClientVersion}, " +
                $"EnabledTabs={(EnabledTabs == null ? "(all)" : string.Join(",", EnabledTabs))}");
        }
    }
}
