using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VantageWorkstationPlus.Services
{
    /// <summary>瞬时错误指数退避重试：默认 3 次（最多等 0.5 + 1 + 2 = 3.5 秒）。
    /// 业务错误（含 InvalidCredentialException、明确 4xx）不重试，立即抛出。</summary>
    public static class RetryPolicy
    {
        public const int DefaultMaxAttempts = 3;
        public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(500);

        /// <summary>指数退避执行 op；只对"瞬时"错误重试。</summary>
        public static async Task<T> ExecuteAsync<T>(Func<Task<T>> op,
            int maxAttempts = DefaultMaxAttempts, CancellationToken ct = default,
            Action<int, Exception>? onRetry = null)
        {
            TimeSpan delay = InitialDelay;
            for (int attempt = 1; ; attempt++)
            {
                try { return await op(); }
                catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
                {
                    onRetry?.Invoke(attempt, ex);
                    await Task.Delay(delay, ct);
                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
                }
            }
        }

        public static async Task ExecuteAsync(Func<Task> op,
            int maxAttempts = DefaultMaxAttempts, CancellationToken ct = default,
            Action<int, Exception>? onRetry = null)
        {
            await ExecuteAsync(async () => { await op(); return 0; }, maxAttempts, ct, onRetry);
        }

        /// <summary>判定错误是否瞬时（可重试）。</summary>
        public static bool IsTransient(Exception ex) => Classify(ex) == ErrorCategory.Transient;

        public enum ErrorCategory { Transient, Auth, Business, Fatal }

        /// <summary>错误分类，UI 据此着色/决定下一步行为。</summary>
        public static ErrorCategory Classify(Exception ex)
        {
            if (ex is HttpRequestException) return ErrorCategory.Transient;
            if (ex is TaskCanceledException) return ErrorCategory.Transient;
            if (ex is System.Net.Sockets.SocketException) return ErrorCategory.Transient;
            if (ex is System.Security.Authentication.InvalidCredentialException) return ErrorCategory.Auth;

            string m = ex.Message ?? "";
            if (m.Contains("HTTP 502") || m.Contains("HTTP 503") || m.Contains("HTTP 504")
                || m.Contains("HTTP 408") || m.Contains("HTTP 429"))
                return ErrorCategory.Transient;
            if (m.Contains("HTTP 401") || m.Contains("HTTP 403") || m.Contains("未注册")
                || m.Contains("AuthHeader"))
                return ErrorCategory.Auth;
            if (m.Contains("HTTP 4")) return ErrorCategory.Business;
            return ErrorCategory.Fatal;
        }

        /// <summary>给 UI 用的颜色 hex（与 ModernTheme 协调）。</summary>
        public static string CategoryColor(ErrorCategory c) => c switch
        {
            ErrorCategory.Transient => "#9D5D00",   // 橙黄 = 重试中
            ErrorCategory.Auth => "#B5281A",        // 红 = 严重
            ErrorCategory.Business => "#5C6470",    // 灰 = 跳过
            _ => "#B5281A",
        };

        public static string CategoryIcon(ErrorCategory c) => c switch
        {
            ErrorCategory.Transient => "🌐",
            ErrorCategory.Auth => "🔒",
            ErrorCategory.Business => "📋",
            _ => "💥",
        };
    }
}
