using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VantageWorkstationPlus
{
    /// <summary>持久 HTTP 会话 + ASP.NET WebForms 自动登录 + JSON 调用。</summary>
    public class AppSession : IDisposable
    {
        private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

        public string BaseUrl { get; }
        public string Username { get; private set; } = "";

        private readonly CookieContainer _cookies = new();
        private readonly HttpClient _http;

        public AppSession(string baseUrl, bool acceptAnyServerCert = true)
        {
            BaseUrl = baseUrl.TrimEnd('/');
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                AllowAutoRedirect = false,
                UseCookies = true,
            };
            if (acceptAnyServerCert)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
        }

        public void Dispose() => _http.Dispose();

        public async Task LoginAsync(string user, string pwd)
        {
            string loginUrl = $"{BaseUrl}/navify.Advantage.Web/Login.aspx";

            // 1) GET 拿 ViewState 等
            using var get = new HttpRequestMessage(HttpMethod.Get, loginUrl);
            get.Headers.Accept.ParseAdd("text/html");
            var getResp = await _http.SendAsync(get);
            getResp.EnsureSuccessStatusCode();
            string html = await getResp.Content.ReadAsStringAsync();

            string Extract(string name)
            {
                var m = Regex.Match(html,
                    $@"<input[^>]*name=""{Regex.Escape(name)}""[^>]*value=""([^""]*)""",
                    RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value : "";
            }
            string viewstate = Extract("__VIEWSTATE");
            string generator = Extract("__VIEWSTATEGENERATOR");
            string eventval = Extract("__EVENTVALIDATION");
            if (string.IsNullOrEmpty(viewstate) || string.IsNullOrEmpty(eventval))
                throw new InvalidOperationException("登录页未解析到 __VIEWSTATE / __EVENTVALIDATION");

            // 2) POST 凭据
            var form = new Dictionary<string, string>
            {
                ["__LASTFOCUS"] = "",
                ["__EVENTTARGET"] = "btnLogIn",
                ["__EVENTARGUMENT"] = "",
                ["__VIEWSTATE"] = viewstate,
                ["__VIEWSTATEGENERATOR"] = generator,
                ["__VIEWSTATEENCRYPTED"] = "",
                ["__EVENTVALIDATION"] = eventval,
                ["txtUserName"] = user,
                ["txtPassword"] = pwd,
            };
            using var post = new HttpRequestMessage(HttpMethod.Post, loginUrl)
            {
                Content = new FormUrlEncodedContent(form),
            };
            post.Headers.Referrer = new Uri(loginUrl);
            post.Headers.Accept.ParseAdd("text/html");
            var postResp = await _http.SendAsync(post);

            if ((int)postResp.StatusCode is 301 or 302 or 303)
            {
                // 登录成功
            }
            else if (postResp.StatusCode == HttpStatusCode.OK)
            {
                string body = await postResp.Content.ReadAsStringAsync();
                var m = Regex.Match(body,
                    @"<span[^>]*id=""[^""]*(lblError|lblMessage)[^""]*""[^>]*>([^<]+)</span>",
                    RegexOptions.IgnoreCase);
                throw new InvalidOperationException(
                    "登录失败: " + (m.Success ? m.Groups[2].Value.Trim() : "用户名或密码错误"));
            }
            else
            {
                throw new InvalidOperationException($"登录失败: HTTP {(int)postResp.StatusCode}");
            }

            var apiAuth = _cookies.GetCookies(new Uri(BaseUrl))["Ventana.Vantage.ApiAuth"];
            if (apiAuth == null)
                throw new InvalidOperationException("登录后未收到 Ventana.Vantage.ApiAuth cookie");
            Username = user;
        }

        /// <summary>POST 到 /Workflow/Folders.aspx/{path}。payload=null 时发空 body（无参 WebMethod 要求）。</summary>
        public async Task<(JToken? Data, string? Error)> PostFoldersAsync(string path, object? payload)
        {
            string url = $"{BaseUrl}/navify.Advantage.Web/Workflow/Folders.aspx/{path}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Accept.ParseAdd("*/*");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Headers.Referrer = new Uri($"{BaseUrl}/navify.Advantage.Web/Workflow/Folders.aspx");

            if (payload == null)
            {
                req.Content = new StringContent("", Encoding.UTF8, "application/json");
            }
            else
            {
                string json = JsonConvert.SerializeObject(payload);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req); }
            catch (Exception ex) { return (null, ex.Message); }

            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                try
                {
                    var err = JObject.Parse(body);
                    return (null, err["Message"]?.ToString() ?? $"HTTP {(int)resp.StatusCode}");
                }
                catch
                {
                    return (null, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}");
                }
            }

            JToken root;
            try { root = JToken.Parse(body); }
            catch { return (null, $"非JSON响应: {Truncate(body, 200)}"); }

            if (root is JObject jo && jo["Message"] != null && jo["ExceptionType"] != null && jo["d"] == null)
                return (null, jo["Message"]?.ToString() ?? "未知错误");

            var d = root["d"];
            if (d == null) return (root, null);
            if (d.Type == JTokenType.String)
            {
                string s = d.ToString();
                try { return (JToken.Parse(s), null); }
                catch { return (JValue.CreateString(s), null); }
            }
            return (d, null);
        }

        /// <summary>透传 HttpRequestMessage（带 cookie）。给特殊请求用，如 SpecimenTracking 的 form-urlencoded 回传。</summary>
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage req) => _http.SendAsync(req);

        /// <summary>带登录态 GET 一个页面（如抽下拉框 HTML）。</summary>
        public async Task<string> GetHtmlAsync(string relativeOrAbsoluteUrl)
        {
            string url = relativeOrAbsoluteUrl.StartsWith("http")
                ? relativeOrAbsoluteUrl
                : $"{BaseUrl}{(relativeOrAbsoluteUrl.StartsWith("/") ? "" : "/")}{relativeOrAbsoluteUrl}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("text/html");
            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync();
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
    }
}
