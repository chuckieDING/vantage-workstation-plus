using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VantageWorkstationPlus.Models;

namespace VantageWorkstationPlus.Services
{
    public static class FoldersApi
    {
        /// <summary>扫一张玻片，返回归一化 ID + InsertTs。</summary>
        public static async Task<SlideScanOutcome> ScanSlideAsync(AppSession s, string slideId)
        {
            var (data, err) = await s.PostFoldersAsync("ScanObject", new { scanStr = slideId });
            var r = new SlideScanOutcome { InputId = slideId };
            if (err != null) { r.Error = err; return r; }
            if (data is not JObject o) { r.Error = "响应非对象"; return r; }
            var error = o["Error"]?.ToString();
            if (!string.IsNullOrEmpty(error)) { r.Error = error; return r; }

            var caseview = o["caseview"] as JArray;
            JObject? match = null;
            if (caseview != null)
            {
                foreach (var item in caseview.OfType<JObject>())
                {
                    if (string.IsNullOrEmpty(item["InsertTs"]?.ToString())) continue;
                    var ext = item["ExtSlideId"]?.ToString();
                    var lis = item["LisSlideId"]?.ToString();
                    if (ext == slideId || lis == slideId) { match = item; break; }
                }
            }
            if (match == null) { r.Error = "未返回 InsertTs（玻片可能不存在）"; return r; }

            r.Ok = true;
            r.LisSlideId = match["LisSlideId"]?.ToString() ?? "";
            r.InsertTs = match["InsertTs"]?.ToString() ?? "";
            r.LisPathId = match["LisPathId"]?.ToObject<int?>();
            return r;
        }

        /// <summary>扫位置条码，返回 (Location, Pathologists, Error)。</summary>
        public static async Task<(Location? Loc, List<Pathologist> Paths, string? Error)>
            ScanLocationAsync(AppSession s, string barcode)
        {
            var (data, err) = await s.PostFoldersAsync("ScanObject", new { scanStr = barcode });
            if (err != null) return (null, new(), err);
            if (data is not JObject o) return (null, new(), "响应非对象");
            var error = o["Error"]?.ToString();
            if (!string.IsNullOrEmpty(error)) return (null, new(), error);

            var loc = o["location"]?.ToObject<Location>();
            if (loc == null || loc.LocationId == 0) return (null, new(), $"位置条码 {barcode} 无效");
            var paths = o["pathologist"]?.ToObject<List<Pathologist>>() ?? new();
            return (loc, paths, null);
        }

        /// <summary>无位置过滤的全部医生。</summary>
        public static async Task<List<Pathologist>> GetAllPathologistsAsync(AppSession s)
        {
            var (data, err) = await s.PostFoldersAsync("GetPathologistsWithoutFacility", null);
            if (err != null) throw new System.Exception($"获取医生列表失败: {err}");
            if (data is JArray arr) return arr.ToObject<List<Pathologist>>() ?? new();
            if (data is JObject jo) return jo["pathologist"]?.ToObject<List<Pathologist>>() ?? new();
            throw new System.Exception($"响应类型异常: {data?.Type}");
        }

        /// <summary>提交批量签发。</summary>
        public static async Task<(JToken? Data, string? Error)> SaveAndSignOffAsync(
            AppSession s, List<string> lisSlides, List<string> timeStamps, int locId, int pathId)
        {
            return await s.PostFoldersAsync("SaveAndSignOffFolder", new SignOffRequest
            {
                fldrId = -1,
                lisSlides = lisSlides,
                timeStamps = timeStamps,
                locId = locId,
                pathId = pathId,
            });
        }

        /// <summary>从 Folders.aspx 页面 HTML 抽位置下拉列表。</summary>
        public static async Task<List<(string Barcode, string Name)>> GetLocationsAsync(AppSession s)
        {
            string html = await s.GetHtmlAsync("/navify.Advantage.Web/Workflow/Folders.aspx");
            var blockMatch = System.Text.RegularExpressions.Regex.Match(html,
                @"<select[^>]*id=""[^""]*ddlLocation""[^>]*>(.*?)</select>",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var result = new List<(string, string)>();
            if (!blockMatch.Success) return result;
            string block = blockMatch.Groups[1].Value;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(block,
                @"<option[^>]*value=""([^""]+)""[^>]*>(.*?)</option>",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string val = m.Groups[1].Value;
                if (val == "-1") continue;
                string name = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                result.Add((val, name));
            }
            return result;
        }
    }
}
