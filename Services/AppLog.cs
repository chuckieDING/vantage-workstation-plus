using System;
using System.IO;

namespace VantageWorkstationPlus.Services
{
    /// <summary>极简文件日志：./logs/yyyy-MM-dd.log（同 EXE 目录）。线程不安全，按行追加。</summary>
    public static class AppLog
    {
        private static readonly object _lock = new();
        private static string? _logPath;

        public static string LogPath
        {
            get
            {
                if (_logPath != null) return _logPath;
                string dir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");
                return _logPath;
            }
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg) => Write("ERROR", msg);
        public static void Error(string msg, Exception ex) =>
            Write("ERROR", msg + "\n" + ex);

        private static void Write(string level, string msg)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch { /* 写日志失败本身不能再抛 */ }
        }
    }
}
