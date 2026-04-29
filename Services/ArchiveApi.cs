using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace VantageWorkstationPlus.Services
{
    /// <summary>SpecimenArchive 多步流程：
    /// 1. GET 页面拿初始 ViewState
    /// 2. POST txtCase 加载病例的物品网格（响应里带归档位置下拉 + 未/已归档行）
    /// 3. 对每个要归档的物品 POST lnkArchived（带 5 个 hdn 隐藏字段 + ddlLocation）
    /// 4. POST btnSave 提交
    /// </summary>
    public class ArchiveSession
    {
        private const string PagePath = "/navify.Advantage.Web/Workflow/SpecimenArchive.aspx";

        private readonly AppSession _session;
        private string _viewState = "";
        private string _viewStateGenerator = "";
        private string _eventValidation = "";
        private string _previousPage = "";

        public List<(int Id, string Name)> Locations { get; } = new();
        public List<ArtifactRow> NotArchived { get; } = new();
        public List<ArtifactRow> Archived { get; } = new();

        /// <summary>最近一次响应原文，调试用。</summary>
        public string LastResponseBody { get; private set; } = "";

        public ArchiveSession(AppSession session) { _session = session; }

        /// <summary>初始 GET。响应只含 __VIEWSTATE 三件套，没有位置下拉和网格。</summary>
        public async Task InitializeAsync()
        {
            string html = await _session.GetHtmlAsync(PagePath);
            _viewState = MatchInputValue(html, "__VIEWSTATE") ?? "";
            _viewStateGenerator = MatchInputValue(html, "__VIEWSTATEGENERATOR") ?? "";
            _eventValidation = MatchInputValue(html, "__EVENTVALIDATION") ?? "";
            if (string.IsNullOrEmpty(_viewState) || string.IsNullOrEmpty(_eventValidation))
                throw new InvalidOperationException("SpecimenArchive 页未解析到 __VIEWSTATE / __EVENTVALIDATION");
        }

        /// <summary>用一个物品 ID（玻片或蜡块）查询其所属 case 的全部物品。
        /// 响应会更新 ViewState、Locations、NotArchived、Archived。</summary>
        public async Task LoadCaseAsync(string itemIdOrCaseId)
        {
            var form = BaseForm("ctl00_ContentPlaceHolder_txtCase");
            form["ctl00$ScriptManager"] = "ctl00$ContentPlaceHolder$upTopPanel|ctl00_ContentPlaceHolder_txtCase";
            form["ctl00$ContentPlaceHolder$txtCase"] = itemIdOrCaseId;
            form["hdIsUnsavedChanges"] = "";

            string body = await PostAsync(form);
            UpdateTokensFromAjaxResponse(body);
            ParseLocations(body);
            ParseGridRows(body);
        }

        /// <summary>对单个未归档物品标记归档（不提交）。</summary>
        public async Task<string?> MarkForArchiveAsync(ArtifactRow row, int locationId, string caseTextValue)
        {
            var form = BaseForm("ctl00$ContentPlaceHolder$lnkArchived");
            form["ctl00$ScriptManager"] = "ctl00$ContentPlaceHolder$upGridPanel|ctl00$ContentPlaceHolder$lnkArchived";
            form["ctl00$ContentPlaceHolder$txtCase"] = caseTextValue;
            form["ctl00$ContentPlaceHolder$ddlLocation"] = locationId.ToString();
            form["ctl00$ContentPlaceHolder$hdnArtifactText"] = row.ArtifactText;
            form["ctl00$ContentPlaceHolder$hdnArtifactValue"] = row.ArtifactValue;
            form["ctl00$ContentPlaceHolder$hdnArtifactLocation"] = "";
            form["ctl00$ContentPlaceHolder$hdnLISspecID"] = row.LISSpecID;
            form["ctl00$ContentPlaceHolder$hdnArtifactType"] = row.ArtifactType;
            form["hdIsUnsavedChanges"] = "1";
            if (!string.IsNullOrEmpty(_previousPage))
                form["__PREVIOUSPAGE"] = _previousPage;

            string body = await PostAsync(form);
            UpdateTokensFromAjaxResponse(body);
            ParseGridRows(body);
            return ExtractError(body);
        }

        /// <summary>提交所有标记入库。</summary>
        public async Task<string?> SaveAsync(string caseTextValue, int locationId)
        {
            var form = BaseForm("ctl00$ContentPlaceHolder$btnSave");
            form["ctl00$ScriptManager"] = "ctl00$ContentPlaceHolder$upPanelSCButtons|ctl00$ContentPlaceHolder$btnSave";
            form["ctl00$ContentPlaceHolder$txtCase"] = caseTextValue;
            form["ctl00$ContentPlaceHolder$ddlLocation"] = locationId.ToString();
            form["hdIsUnsavedChanges"] = "0";
            if (!string.IsNullOrEmpty(_previousPage))
                form["__PREVIOUSPAGE"] = _previousPage;

            string body = await PostAsync(form);
            UpdateTokensFromAjaxResponse(body);
            return ExtractError(body);
        }

        // ---------- 内部 ----------

        private Dictionary<string, string> BaseForm(string eventTarget) => new()
        {
            ["ctl00$SearchTextBox"] = "",
            ["ctl00$ContentPlaceHolder$hdnArtifactText"] = "",
            ["ctl00$ContentPlaceHolder$hdnArtifactValue"] = "",
            ["ctl00$ContentPlaceHolder$hdnArtifactLocation"] = "",
            ["ctl00$ContentPlaceHolder$hdnLISspecID"] = "",
            ["ctl00$ContentPlaceHolder$hdnArtifactType"] = "",
            ["hdLogoutWarning"] = "",
            ["hdnArtifactAlert"] = "",
            ["hdnNotArtifactAlert"] = "",
            ["hdnSelectLocation"] = "",
            ["hdUnsavedChangesMsg"] = "",
            ["__EVENTTARGET"] = eventTarget,
            ["__EVENTARGUMENT"] = "",
            ["__VIEWSTATE"] = _viewState,
            ["__VIEWSTATEGENERATOR"] = _viewStateGenerator,
            ["__VIEWSTATEENCRYPTED"] = "",
            ["__EVENTVALIDATION"] = _eventValidation,
            ["__ASYNCPOST"] = "true",
        };

        private async Task<string> PostAsync(Dictionary<string, string> form)
        {
            string url = $"{_session.BaseUrl}{PagePath}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(EncodeForm(form), Encoding.UTF8, "application/x-www-form-urlencoded"),
            };
            req.Headers.Add("X-MicrosoftAjax", "Delta=true");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Headers.Referrer = new Uri(url);
            req.Headers.Accept.ParseAdd("*/*");
            var resp = await _session.SendAsync(req);
            return await resp.Content.ReadAsStringAsync();
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

        private static string? MatchInputValue(string html, string name)
        {
            var m = Regex.Match(html,
                $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private void UpdateTokensFromAjaxResponse(string body)
        {
            string? v = ExtractAjaxField(body, "__VIEWSTATE");
            if (v != null) _viewState = v;
            v = ExtractAjaxField(body, "__VIEWSTATEGENERATOR");
            if (v != null) _viewStateGenerator = v;
            v = ExtractAjaxField(body, "__EVENTVALIDATION");
            if (v != null) _eventValidation = v;
            v = ExtractAjaxField(body, "__PREVIOUSPAGE");
            if (v != null) _previousPage = v;
        }

        private static string? ExtractAjaxField(string body, string name)
        {
            int idx = body.IndexOf("|hiddenField|" + name + "|", StringComparison.Ordinal);
            if (idx < 0) return null;
            int valStart = idx + ("|hiddenField|" + name + "|").Length;
            int valEnd = body.IndexOf('|', valStart);
            if (valEnd < 0) return null;
            return body.Substring(valStart, valEnd - valStart);
        }

        private void ParseLocations(string body)
        {
            Locations.Clear();
            var blockMatch = Regex.Match(body,
                @"<select[^>]*id=""[^""]*ddlLocation""[^>]*>(.*?)</select>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!blockMatch.Success) return;
            foreach (Match m in Regex.Matches(blockMatch.Groups[1].Value,
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

        private void ParseGridRows(string body)
        {
            NotArchived.Clear();
            Archived.Clear();
            ParseGridTable(body, "gvNotArchived", NotArchived);
            ParseGridTable(body, "gvArchived", Archived);
        }

        private static void ParseGridTable(string body, string tableId, List<ArtifactRow> bucket)
        {
            // 找到该 table 的 HTML 段
            var tableMatch = Regex.Match(body,
                $"<table[^>]*id=\"[^\"]*{Regex.Escape(tableId)}\"[^>]*>(.*?)</table>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!tableMatch.Success) return;
            string content = tableMatch.Groups[1].Value;
            // 解析 fn_selectRow(this, "text", "value", "loc", "lis", "type")
            // HTML 编码：&quot;
            foreach (Match m in Regex.Matches(content,
                @"fn_selectRow\s*\(\s*this\s*,\s*&quot;([^&]*)&quot;\s*,\s*&quot;([^&]*)&quot;\s*,\s*&quot;([^&]*)&quot;\s*,\s*&quot;([^&]*)&quot;\s*,\s*&quot;([^&]*)&quot;",
                RegexOptions.IgnoreCase))
            {
                bucket.Add(new ArtifactRow
                {
                    ArtifactText = HttpUtility.HtmlDecode(m.Groups[1].Value),
                    ArtifactValue = HttpUtility.HtmlDecode(m.Groups[2].Value),
                    ArtifactLocation = HttpUtility.HtmlDecode(m.Groups[3].Value),
                    LISSpecID = HttpUtility.HtmlDecode(m.Groups[4].Value),
                    ArtifactType = HttpUtility.HtmlDecode(m.Groups[5].Value),
                });
            }
        }

        private static string? ExtractError(string body)
        {
            foreach (var id in new[] { "lblErrorMessage", "lblMessage" })
            {
                var m = Regex.Match(body,
                    $"<span[^>]*id=\"[^\"]*{id}\"[^>]*>(.*?)</span>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string text = HttpUtility.HtmlDecode(m.Groups[1].Value).Trim();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            return null;
        }
    }

    public class ArtifactRow
    {
        public string ArtifactText = "";
        public string ArtifactValue = "";
        public string ArtifactLocation = "";
        public string LISSpecID = "";
        public string ArtifactType = "";
    }
}
