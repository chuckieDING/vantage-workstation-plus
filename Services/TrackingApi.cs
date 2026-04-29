using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace VantageWorkstationPlus.Services
{
    /// <summary>SpecimenTracking 页面是 ASP.NET WebForms + UpdatePanel AJAX 回传。
    /// 每次回传响应是管道符分隔格式，需要解析出新的 __VIEWSTATE / __EVENTVALIDATION 给下次用。</summary>
    public class TrackingSession
    {
        private const string PagePath = "/navify.Advantage.Web/Workflow/SpecimenTracking.aspx";

        private readonly AppSession _session;
        private string _viewState = "";
        private string _viewStateGenerator = "";
        private string _eventValidation = "";

        public List<(int Id, string Name)> Locations { get; } = new();

        public TrackingSession(AppSession session) { _session = session; }

        /// <summary>GET 页面，取出 ViewState/EventValidation 三件套和位置下拉。</summary>
        public async Task InitializeAsync()
        {
            string html = await _session.GetHtmlAsync(PagePath);
            ParseTokensFromHtml(html);
            ParseLocations(html);
        }

        /// <summary>提交一个对象到指定位置。</summary>
        public async Task<TrackOutcome> TrackAsync(int locationId, string objectId)
        {
            string url = $"{_session.BaseUrl}{PagePath}";
            var form = new Dictionary<string, string>
            {
                ["ctl00$ScriptManager"] = "ctl00$ContentPlaceHolder$upPanelArchiveLocation|ctl00$ContentPlaceHolder$txtObject",
                ["ctl00$SearchTextBox"] = "",
                ["ctl00$ContentPlaceHolder$ddlLocation"] = locationId.ToString(),
                ["ctl00$ContentPlaceHolder$txtObject"] = objectId,
                ["hdLogoutWarning"] = "",
                ["hdIsUnsavedChanges"] = "1",
                ["hdUnsavedChangesMsg"] = "",
                ["__LASTFOCUS"] = "",
                ["__EVENTTARGET"] = "ctl00_ContentPlaceHolder_txtObject",
                ["__EVENTARGUMENT"] = "",
                ["__VIEWSTATE"] = _viewState,
                ["__VIEWSTATEGENERATOR"] = _viewStateGenerator,
                ["__VIEWSTATEENCRYPTED"] = "",
                ["__EVENTVALIDATION"] = _eventValidation,
                ["__ASYNCPOST"] = "true",
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(EncodeForm(form), Encoding.UTF8, "application/x-www-form-urlencoded"),
            };
            req.Headers.Add("X-MicrosoftAjax", "Delta=true");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Headers.Referrer = new Uri(url);
            req.Headers.Accept.ParseAdd("*/*");

            string body;
            try
            {
                var resp = await _session.SendAsync(req);
                body = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                return new TrackOutcome { Ok = false, Error = "网络异常: " + ex.Message };
            }

            // 更新 ViewState 等以备下次
            UpdateTokensFromAjaxResponse(body);

            // 解析错误：lblErrorMsg 和 lblMessage 任一非空就算失败
            string lblErrorMsg = ExtractSpan(body, "ctl00_ContentPlaceHolder_lblErrorMsg");
            string lblMessage = ExtractSpan(body, "ctl00_ContentPlaceHolder_lblMessage");
            string err = !string.IsNullOrEmpty(lblErrorMsg) ? lblErrorMsg
                        : !string.IsNullOrEmpty(lblMessage) ? lblMessage
                        : "";

            // 检查 lstEnteredObjects 是否包含本次扫的 ID 来判断是否成功入列
            var entered = ExtractEnteredObjects(body);
            bool inList = entered.Contains(objectId);

            return new TrackOutcome
            {
                Ok = string.IsNullOrEmpty(err) && inList,
                Error = err,
                AcceptedObjects = entered,
            };
        }

        // ---------- 解析 ----------

        private void ParseTokensFromHtml(string html)
        {
            _viewState = MatchInputValue(html, "__VIEWSTATE") ?? "";
            _viewStateGenerator = MatchInputValue(html, "__VIEWSTATEGENERATOR") ?? "";
            _eventValidation = MatchInputValue(html, "__EVENTVALIDATION") ?? "";
            if (string.IsNullOrEmpty(_viewState) || string.IsNullOrEmpty(_eventValidation))
                throw new InvalidOperationException("SpecimenTracking 页面未解析到 ViewState / EventValidation");
        }

        private static string? MatchInputValue(string html, string name)
        {
            var m = Regex.Match(html,
                $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(html,
                $"<input[^>]*value=\"([^\"]*)\"[^>]*name=\"{Regex.Escape(name)}\"",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private void ParseLocations(string html)
        {
            Locations.Clear();
            var blockMatch = Regex.Match(html,
                @"<select[^>]*id=""[^""]*ddlLocation""[^>]*>(.*?)</select>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!blockMatch.Success) return;
            string block = blockMatch.Groups[1].Value;
            foreach (Match m in Regex.Matches(block,
                @"<option[^>]*value=""(\d+)""[^>]*>(.*?)</option>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                if (int.TryParse(m.Groups[1].Value, out int id))
                {
                    string name = HttpUtility.HtmlDecode(m.Groups[2].Value).Trim();
                    Locations.Add((id, name));
                }
            }
        }

        /// <summary>解析 AJAX 回传响应里的更新字段。
        /// 格式：|size|hiddenField|__VIEWSTATE|value|...</summary>
        private void UpdateTokensFromAjaxResponse(string body)
        {
            string? v = ExtractAjaxField(body, "__VIEWSTATE");
            if (v != null) _viewState = v;
            v = ExtractAjaxField(body, "__VIEWSTATEGENERATOR");
            if (v != null) _viewStateGenerator = v;
            v = ExtractAjaxField(body, "__EVENTVALIDATION");
            if (v != null) _eventValidation = v;
        }

        private static string? ExtractAjaxField(string body, string name)
        {
            // 格式: |<bytes>|hiddenField|<name>|<value>|
            // value 可能很长且含 / 等，但不含未转义的 |
            int idx = body.IndexOf("|hiddenField|" + name + "|", StringComparison.Ordinal);
            if (idx < 0) return null;
            int valStart = idx + ("|hiddenField|" + name + "|").Length;
            int valEnd = body.IndexOf('|', valStart);
            if (valEnd < 0) return null;
            return body.Substring(valStart, valEnd - valStart);
        }

        private static string ExtractSpan(string body, string id)
        {
            var m = Regex.Match(body,
                $"<span[^>]*id=\"{Regex.Escape(id)}\"[^>]*>(.*?)</span>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return m.Success ? HttpUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
        }

        private static List<string> ExtractEnteredObjects(string body)
        {
            var result = new List<string>();
            var blockMatch = Regex.Match(body,
                @"<select[^>]*id=""[^""]*lstEnteredObjects""[^>]*>(.*?)</select>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!blockMatch.Success) return result;
            foreach (Match m in Regex.Matches(blockMatch.Groups[1].Value,
                @"<option[^>]*value=""([^""]+)""", RegexOptions.IgnoreCase))
                result.Add(m.Groups[1].Value);
            return result;
        }

        private static string EncodeForm(Dictionary<string, string> form)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var kv in form)
            {
                if (!first) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
                first = false;
            }
            return sb.ToString();
        }
    }

    public class TrackOutcome
    {
        public bool Ok;
        public string Error = "";
        public List<string> AcceptedObjects = new();
    }
}
