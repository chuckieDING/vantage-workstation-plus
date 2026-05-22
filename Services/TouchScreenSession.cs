using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace VantageWorkstationPlus.Services
{
    /// <summary>SOAP 客户端：navify Advantage Histology Workstation 内部服务。
    /// 抓包路径：/Ventana.Webservices/{TouchScreen,Security}Service.asmx
    /// 鉴权：每个请求 SOAP Header 里塞 AuthHeader（来自 SoapAuth）。</summary>
    public class TouchScreenSession : IDisposable
    {
        private const string TouchScreenPath = "/Ventana.Webservices/TouchScreenService.asmx";
        private const string SecurityPath = "/Ventana.Webservices/SecurityService.asmx";
        private const string ClientSetupPath = "/Ventana.WebServices/ClientSetup.asmx";
        private const string Tempuri = "http://tempuri.org/";
        static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
        static readonly XNamespace TempNs = Tempuri;
        static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

        public string BaseUrl { get; }
        public SoapAuth Auth { get; }
        public LoggedUser? LoggedInUser { get; private set; }
        public string LastResponseBody { get; private set; } = "";

        private readonly HttpClient _http;

        public TouchScreenSession(string baseUrl, SoapAuth auth, bool acceptAnyServerCert = true)
        {
            BaseUrl = baseUrl.TrimEnd('/');
            Auth = auth;
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
            };
            if (acceptAnyServerCert)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/4.0 (compatible; MSIE 6.0; MS Web Services Client Protocol 4.0.30319.42000)");
            _http.DefaultRequestHeaders.ExpectContinue = true;
        }

        public void Dispose() => _http.Dispose();

        // ============== Workstation 首次注册 ==============

        /// <summary>首次握手：本地随机生成 32B Key/Password 并写环境变量；用约定密钥（SHA256 of "V3ntanaNPLAD3faultEncryptionK3y"+host）
        /// 加密 SecurityHandshakePayloadModel JSON，POST 到 SecurityService.StoreEncryptionKeyAndPassword。
        /// 成功后此 SoapAuth 即可用于后续所有 SOAP 调用。</summary>
        public static async Task<TouchScreenSession> RegisterWorkstationAsync(string baseUrl, string clientVersion = "4.1.25136.1")
        {
            string mac = SoapAuth.GetMacAddress();
            var (auth, keyB64, pwdB64) = SoapAuth.GenerateAndStoreLocally(mac);
            auth.ClientVersion = clientVersion;
            var sess = new TouchScreenSession(baseUrl, auth);

            string host = SoapAuth.ExtractHost(baseUrl);
            byte[] knownKey = SoapAuth.ComputeKnownEncryptionKey(host);
            string payloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                MachineEncryptionKey = keyB64,
                GeneratedPassword = pwdB64,
            });
            string encrypted = SoapAuth.EncryptWithKey(payloadJson, knownKey);

            string body =
                $"<securityPayload>{Esc(encrypted)}</securityPayload>" +
                $"<macAddress>{mac}</macAddress>";
            try
            {
                var resp = await sess.PostSoapAsync(SecurityPath, "StoreEncryptionKeyAndPassword", body);
                var ok = resp.Element(TempNs + "StoreEncryptionKeyAndPasswordResult")?.Value?.Trim();
                if (!string.Equals(ok, "true", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"服务器拒绝注册（StoreEncryptionKeyAndPasswordResult={ok}）");
            }
            catch
            {
                SoapAuth.ClearEnvironment(mac);
                throw;
            }
            return sess;
        }

        // ============== ClientSetup（注册 WorkCell 实例） ==============

        public const int WorkCellTypeTissueProcessing = 11;

        /// <summary>在服务器上创建一个 WorkCell（即"工作站实例"）。
        /// 返回服务器分配的 WorkCellID（用于后续所有 SOAP 调用的 workCellId 参数）。</summary>
        public async Task<int> SaveStationAsync(string name, string description,
            string machineGuidPrefix24, int workCellTypeId, int workCellSubType = -1)
        {
            string body =
                $"<name>{Esc(name)}</name>" +
                $"<description>{Esc(description)}</description>" +
                $"<macAddress>{Esc(machineGuidPrefix24)}</macAddress>" +
                $"<workCellTypeId>{workCellTypeId}</workCellTypeId>" +
                $"<workcellSubType>{workCellSubType}</workcellSubType>";
            var resp = await PostSoapAsync(ClientSetupPath, "SaveStation", body);
            var ok = resp.Element(TempNs + "SaveStationResult")?.Value?.Trim();
            if (!string.Equals(ok, "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SaveStation 失败: {ok}");

            // 拉一遍 WorkCells 找新建的（按 MAC 字段匹配）
            var cells = await GetWorkCellsAsync();
            var ours = cells.FirstOrDefault(c =>
                string.Equals(c.MACAddress, machineGuidPrefix24, StringComparison.OrdinalIgnoreCase));
            if (ours == null)
                throw new InvalidOperationException(
                    $"SaveStation 返回 true 但 GetWorkCells 找不到 MAC={machineGuidPrefix24}");
            return ours.WorkCellID;
        }

        public async Task<List<WorkCell>> GetWorkCellsAsync()
        {
            var resp = await PostSoapAsync(ClientSetupPath, "GetWorkCells", "");
            var result = resp.Element(TempNs + "GetWorkCellsResult");
            return result?.Elements(TempNs + "WorkCellInfo").Select(e => new WorkCell
            {
                WorkCellID = ParseInt(e.Element(TempNs + "WorkCellID")?.Value),
                WorkcellName = e.Element(TempNs + "WorkcellName")?.Value?.Trim() ?? "",
                WorkcellDesc = e.Element(TempNs + "WorkcellDesc")?.Value?.Trim() ?? "",
                WorkcellTypeID = ParseInt(e.Element(TempNs + "WorkcellTypeID")?.Value),
                MACAddress = e.Element(TempNs + "MACAddress")?.Value?.Trim() ?? "",
            }).ToList() ?? new List<WorkCell>();
        }

        // ============== Login ==============

        /// <summary>登录：依工作站 PasswordRequired 设置自动选 SOAP 方法。
        /// 原版 WPF 在 PasswordRequired=false 时调 ValidateUserNameAndPrivilege（不带密码），
        /// =true 时调 ValidateUserNamePasswordAndPrivilege。我们不知道服务端设置，先试无密码版，
        /// 失败（FailedMissingCredentials / HTTP 500 / FailedUnknownError）再退回带密码版。</summary>
        public async Task<LoggedUser> LoginAsync(string userName, string password,
            string workCellType, int workCellId)
        {
            string noPwdBody = $@"<userName>{Esc(userName)}</userName>
<loggedInUserName/>
<workCellType>{workCellType}</workCellType>
<workCellID>{workCellId}</workCellID>
<isScreenLocked>false</isScreenLocked>";
            try
            {
                return await DoLoginAsync("ValidateUserNameAndPrivilege", noPwdBody, SecurityPath);
            }
            catch (InvalidCredentialException ex) when (
                ex.Message.Contains("FailedMissingCredentials") ||
                ex.Message.Contains("FailedInvalidPassword") ||
                ex.Message.Contains("FailedUnknownError"))
            {
                // 工作站要求密码，改用带密码版
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 500"))
            {
                // 服务端拒了无密码调用（PasswordRequired=true 时常见的 HTTP 500: Unknown）
            }

            string withPwdBody = $@"<userName>{Esc(userName)}</userName>
<password>{Esc(password)}</password>
<loggedInUserName/>
<workCellType>{workCellType}</workCellType>
<workCellID>{workCellId}</workCellID>
<isScreenLocked>false</isScreenLocked>";
            return await DoLoginAsync("ValidateUserNamePasswordAndPrivilege", withPwdBody, SecurityPath);
        }

        private async Task<LoggedUser> DoLoginAsync(string method, string innerXml, string asmxPath)
        {
            var resp = await PostSoapAsync(asmxPath, method, innerXml);
            var result = resp.Element(TempNs + $"{method}Result")?.Value?.Trim();
            // SucceededXxx 系列都算成功
            if (!result?.StartsWith("Succeeded", StringComparison.OrdinalIgnoreCase) ?? true)
                throw new InvalidCredentialException($"SOAP 登录失败: {result ?? "(无 Result)"}");
            var info = resp.Element(TempNs + "userInfo");
            var u = new LoggedUser
            {
                Id = ParseInt(info?.Element(TempNs + "ID")?.Value),
                UserName = info?.Element(TempNs + "UserName")?.Value?.Trim() ?? "",
                FirstName = info?.Element(TempNs + "FirstName")?.Value?.Trim() ?? "",
                LastName = info?.Element(TempNs + "LastName")?.Value?.Trim() ?? "",
            };
            var privEl = info?.Element(TempNs + "Privileges")?.Element(TempNs + "Privileges");
            if (privEl != null)
                u.Privileges = privEl.Elements(TempNs + "PrivilegeInfo")
                    .Select(p => ParseInt(p.Element(TempNs + "ID")?.Value))
                    .Where(x => x > 0).ToList();
            LoggedInUser = u;
            return u;
        }

        // ============== Tissue Processing 主流程 ==============

        public async Task<List<TissueProcessor>> GetTissueProcessorsAsync()
        {
            var resp = await PostSoapAsync(TouchScreenPath, "GetTissueProcessors", "");
            var result = resp.Element(TempNs + "GetTissueProcessorsResult");
            return result?.Elements(TempNs + "TissueProcessorInfo").Select(ToProcessor).ToList()
                ?? new List<TissueProcessor>();
        }

        public async Task<Retort?> GetRetortInfoAsync(int processorId, int retortNumber)
        {
            var resp = await PostSoapAsync(TouchScreenPath, "GetRetortInfo",
                $"<tissueProcessorId>{processorId}</tissueProcessorId><retortNumber>{retortNumber}</retortNumber>");
            var r = resp.Element(TempNs + "GetRetortInfoResult");
            return r == null ? null : ToRetort(r);
        }

        /// <summary>用用户可见的 basket ID（如扫码字符串）查询。返回 IsEntryExists=false 表示不是篮。</summary>
        public async Task<BasketLookup> GetBasketIDDetailsAsync(string basketDisplayId,
            int workCellId, int empUserId)
        {
            var resp = await PostSoapAsync(TouchScreenPath, "GetBasketIDDetails",
                $"<basketid>{Esc(basketDisplayId)}</basketid><isScannedEntry>true</isScannedEntry>" +
                $"<workCellID>{workCellId}</workCellID><empUserID>{empUserId}</empUserID>");
            var r = resp.Element(TempNs + "GetBasketIDDetailsResult");
            return new BasketLookup
            {
                BasketId = ParseInt(r?.Element(TempNs + "BasketId")?.Value),
                Capacity = ParseInt(r?.Element(TempNs + "Capacity")?.Value),
                ProcessorId = ParseInt(r?.Element(TempNs + "ProcessorId")?.Value),
                RetortNumber = ParseInt(r?.Element(TempNs + "RetortNumber")?.Value),
                IsEntryExists = ParseBool(r?.Element(TempNs + "IsEntryExists")?.Value),
            };
        }

        /// <summary>列出所有可分配给脱水流程的篮（含 HumanReadableId 用于显示）。</summary>
        public async Task<List<BasketInfo>> GetBasketForTPListAsync()
        {
            var resp = await PostSoapAsync(TouchScreenPath, "GetBasketForTPList", "");
            var result = resp.Element(TempNs + "GetBasketForTPListResult");
            return result?.Elements(TempNs + "BasketInfo").Select(ToBasket).ToList()
                ?? new List<BasketInfo>();
        }

        public async Task<BasketInfo?> GetBasketInfoByIdAsync(int basketId)
        {
            var resp = await PostSoapAsync(TouchScreenPath, "GetBasketInfoById",
                $"<basketId>{basketId}</basketId>");
            var r = resp.Element(TempNs + "GetBasketInfoByIdResult");
            return r == null ? null : ToBasket(r);
        }

        private static BasketInfo ToBasket(XElement r)
        {
            var b = new BasketInfo
            {
                BasketId = ParseInt(r.Element(TempNs + "BasketId")?.Value),
                BasketNumber = ParseInt(r.Element(TempNs + "BasketNumber")?.Value),
                Capacity = ParseInt(r.Element(TempNs + "Capacity")?.Value),
                NumberOfUsedCassettes = ParseInt(r.Element(TempNs + "NumberOfUsedCassettes")?.Value),
                ProcessorId = ParseInt(r.Element(TempNs + "ProcessorId")?.Value),
                RetortNumber = ParseInt(r.Element(TempNs + "RetortNumber")?.Value),
                HumanReadableId = r.Element(TempNs + "HumanReadableId")?.Value?.Trim() ?? "",
                BasketName = r.Element(TempNs + "BasketName")?.Value?.Trim() ?? "",
            };
            var cs = r.Element(TempNs + "Cassettes");
            if (cs != null)
                b.Cassettes = cs.Elements(TempNs + "CassetteInfo").Select(c => new Cassette
                {
                    Id = long.TryParse(c.Element(TempNs + "Id")?.Value?.Trim(), out var id) ? id : 0,
                    CaseId = ParseInt(c.Element(TempNs + "CaseId")?.Value),
                    HumanReadableId = c.Element(TempNs + "HumanReadableId")?.Value?.Trim() ?? "",
                    TissueType = c.Element(TempNs + "TissueType")?.Value?.Trim() ?? "",
                    Grosser = c.Element(TempNs + "Grosser")?.Value?.Trim() ?? "",
                }).ToList();
            return b;
        }

        /// <summary>把用户输入的物品 ID（蜡块/玻片）查到 BlockID 等内部 ID。</summary>
        public async Task<Artifact?> GetArtifactAsync(string artifactDisplay, int workCellId, int empUserId)
        {
            var resp = await PostSoapAsync(TouchScreenPath, "GetArtifact",
                $"<artifact>{Esc(artifactDisplay)}</artifact><isScannedEntry>true</isScannedEntry>" +
                $"<workCellID>{workCellId}</workCellID><empUserID>{empUserId}</empUserID>");
            var r = resp.Element(TempNs + "GetArtifactResult");
            if (r == null) return null;
            return new Artifact
            {
                CaseId = ParseInt(r.Element(TempNs + "CaseID")?.Value),
                SpecimenId = ParseInt(r.Element(TempNs + "SpecimenID")?.Value),
                BlockId = ParseInt(r.Element(TempNs + "BlockID")?.Value),
                SearchValue = r.Element(TempNs + "SearchValue")?.Value?.Trim() ?? "",
                ArtifactType = r.Element(TempNs + "ArtifactType")?.Value?.Trim() ?? "",
                HumanReadableId = r.Element(TempNs + "HumanReadableID")?.Value?.Trim() ?? "",
                IsCanceled = ParseBool(r.Element(TempNs + "IsCanceled")?.Value),
            };
        }

        public async Task AssignCassetteToBasketAsync(int cassetteId, int basketId,
            int workCellId, int empUserId)
        {
            await PostSoapAsync(TouchScreenPath, "AssignCassetteToBasket",
                $"<cassetteId>{cassetteId}</cassetteId><basketId>{basketId}</basketId>" +
                $"<workCellId>{workCellId}</workCellId><empUserId>{empUserId}</empUserId>");
        }

        /// <summary>查询篮内当前所有蜡块（GetBasketInfoById 的 Cassettes 字段总返回空，这个才是正确来源）。</summary>
        public async Task<List<WorkItem>> GetTissueProcessingWorkListAsync(int basketId)
        {
            var resp = await PostSoapAsync(TouchScreenPath, "GeTissueProcessingWorkList",
                $"<basketId>{basketId}</basketId>");
            var items = resp.Element(TempNs + "GeTissueProcessingWorkListResult")
                ?.Element(TempNs + "ItemsSource");
            return items?.Elements(TempNs + "WorkItemInfo").Select(w => new WorkItem
            {
                BlockId = long.TryParse(w.Element(TempNs + "BlockID")?.Value?.Trim(), out var b) ? b : 0,
                CaseId = ParseInt(w.Element(TempNs + "CaseID")?.Value),
                CassetteId = w.Element(TempNs + "Cassetteid")?.Value?.Trim() ?? "",
                TissueName = w.Element(TempNs + "TissueNm")?.Value?.Trim() ?? "",
                GrossingLocation = w.Element(TempNs + "GrossingLocation")?.Value?.Trim() ?? "",
                GrossedUser = w.Element(TempNs + "GrossedUser")?.Value?.Trim() ?? "",
            }).ToList() ?? new List<WorkItem>();
        }

        public async Task AssignBasketToRetortAsync(int basketId, int processorId, int retortNumber,
            int retortId, int workCellId, int userId)
        {
            await PostSoapAsync(TouchScreenPath, "AssignBasketToRetort",
                $"<basketId>{basketId}</basketId><processorId>{processorId}</processorId>" +
                $"<retortNumber>{retortNumber}</retortNumber><retortId>{retortId}</retortId>" +
                $"<workCellId>{workCellId}</workCellId><userId>{userId}</userId>");
        }

        /// <summary>开始/排队脱水。startNow=true 立即启动；false 仅排队。</summary>
        public async Task UpdateRetortProcessingStateAsync(int retortId, int durationInMinutes, bool startNow)
        {
            await PostSoapAsync(TouchScreenPath, "UpdateRetortProcessingState",
                $"<retortId>{retortId}</retortId><durationInMinutes>{durationInMinutes}</durationInMinutes>" +
                $"<startNow>{(startNow ? "true" : "false")}</startNow>");
        }

        /// <summary>把 retort 内所有蜡块标为已处理（强制结束时用）。</summary>
        public async Task SetRetortCassettesAsProcessedAsync(int retortId)
        {
            await PostSoapAsync(TouchScreenPath, "SetRetortCassettesAsProcessed",
                $"<retortId>{retortId}</retortId>");
        }

        /// <summary>从篮中移除指定蜡块（编辑篮内容时用，比如未启动前撤掉）。</summary>
        public async Task RemoveCassettesFromBasketAsync(IEnumerable<long> cassetteIds,
            int workCellId, int empUserId, int processorId, int retortNumber)
        {
            string ids = string.Concat(cassetteIds.Select(id => $"<long>{id}</long>"));
            await PostSoapAsync(TouchScreenPath, "RemoveCassettesFromBasket",
                $"<cassetteIds>{ids}</cassetteIds>" +
                $"<workCellID>{workCellId}</workCellID><empUserID>{empUserId}</empUserID>" +
                $"<processorId>{processorId}</processorId><retortNumber>{retortNumber}</retortNumber>");
        }

        /// <summary>结束脱水时把已处理的蜡块标为已从篮中移除（WPF 用这个，不是 RemoveBasketFromRetort）。</summary>
        public async Task SetCassettesRemovedFromBasketAsync(IEnumerable<long> cassetteIds,
            int workCellId, int empUserId, int processorId, int retortNumber)
        {
            string ids = string.Concat(cassetteIds.Select(id => $"<long>{id}</long>"));
            await PostSoapAsync(TouchScreenPath, "SetCassettesRemovedFromBasket",
                $"<cassetteIds>{ids}</cassetteIds>" +
                $"<workCellID>{workCellId}</workCellID><empUserID>{empUserId}</empUserID>" +
                $"<processorId>{processorId}</processorId><retortNumber>{retortNumber}</retortNumber>");
        }

        // ============== HTTP / SOAP 底座 ==============

        private async Task<XElement> PostSoapAsync(string path, string method, string innerBody)
        {
            string envelope =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
                "<soap:Header>" + Auth.BuildAuthHeaderXml() + "</soap:Header>" +
                "<soap:Body>" +
                $"<{method} xmlns=\"{Tempuri}\">{innerBody}</{method}>" +
                "</soap:Body></soap:Envelope>";

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}")
            {
                Content = new StringContent(envelope, Encoding.UTF8),
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
            req.Headers.Add("SOAPAction", $"\"{Tempuri}{method}\"");

            var resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            LastResponseBody = body;
            if (!resp.IsSuccessStatusCode)
            {
                string fault = TryExtractSoapFault(body) ?? Truncate(body, 1500);
                throw new InvalidOperationException($"SOAP {method} 失败 HTTP {(int)resp.StatusCode}: {fault}");
            }

            var doc = XDocument.Parse(body);
            var soapBody = doc.Root!.Element(SoapNs + "Body")
                ?? throw new InvalidOperationException($"SOAP 响应缺 Body: {Truncate(body, 200)}");
            var fault2 = soapBody.Element(SoapNs + "Fault");
            if (fault2 != null)
                throw new InvalidOperationException(
                    $"SOAP {method} Fault: {fault2.Element("faultstring")?.Value ?? fault2.Value}");
            var responseEl = soapBody.Element(TempNs + $"{method}Response")
                ?? soapBody.Elements().FirstOrDefault()
                ?? throw new InvalidOperationException($"SOAP 响应缺 {method}Response");
            return responseEl;
        }

        private static string? TryExtractSoapFault(string body)
        {
            try
            {
                var d = XDocument.Parse(body);
                var fs = d.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
                var detail = d.Descendants().FirstOrDefault(e => e.Name.LocalName == "detail");
                string detailText = detail == null ? "" :
                    "\nDetail: " + Truncate(string.Concat(detail.DescendantNodes()
                        .Where(n => n is XText).Select(n => ((XText)n).Value)).Trim(), 800);
                if (!string.IsNullOrEmpty(fs)) return fs + detailText;
                return null;
            }
            catch { return null; }
        }

        // ============== Helpers ==============

        private static TissueProcessor ToProcessor(XElement p)
        {
            var tp = new TissueProcessor
            {
                Id = ParseInt(p.Element(TempNs + "TissueProcessorId")?.Value),
                Name = p.Element(TempNs + "TissueProcessorName")?.Value?.Trim() ?? "",
                NumberOfRetorts = ParseInt(p.Element(TempNs + "NumberOfRetorts")?.Value),
                NumberOfBaskets = ParseInt(p.Element(TempNs + "NumberOfBaskets")?.Value),
                IsActive = ParseBool(p.Element(TempNs + "IsActive")?.Value),
            };
            var retorts = p.Element(TempNs + "Retorts");
            if (retorts != null)
                tp.Retorts = retorts.Elements(TempNs + "RetortInfo").Select(ToRetort).ToList();
            return tp;
        }

        private static Retort ToRetort(XElement r)
        {
            var ret = new Retort
            {
                Id = ParseInt(r.Element(TempNs + "RetortId")?.Value),
                ProcessorId = ParseInt(r.Element(TempNs + "ProcessorId")?.Value),
                ProcessorName = r.Element(TempNs + "ProcessorName")?.Value?.Trim() ?? "",
                Number = ParseInt(r.Element(TempNs + "RetortNumber")?.Value),
                Capacity = ParseInt(r.Element(TempNs + "Capacity")?.Value),
                Duration = ParseInt(r.Element(TempNs + "Duration")?.Value),
                IsInProcess = ParseBool(r.Element(TempNs + "IsInProcess")?.Value),
                StartTime = ParseDate(r.Element(TempNs + "StartTime")?.Value),
            };
            var baskets = r.Element(TempNs + "Baskets");
            if (baskets != null)
                ret.Baskets = baskets.Elements(TempNs + "BasketInfo").Select(ToBasket).ToList();
            return ret;
        }

        private static int ParseInt(string? s) =>
            int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        private static bool ParseBool(string? s) =>
            bool.TryParse(s?.Trim(), out var v) && v;
        private static DateTime ParseDate(string? s) =>
            DateTime.TryParse(s?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var v)
                ? v : DateTime.MinValue;

        private static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "");
        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
    }

    public class LoggedUser
    {
        public int Id;
        public string UserName = "";
        public string FirstName = "";
        public string LastName = "";
        public List<int> Privileges = new();
    }

    public class TissueProcessor
    {
        public int Id;
        public string Name = "";
        public int NumberOfRetorts;
        public int NumberOfBaskets;
        public bool IsActive;
        public List<Retort> Retorts = new();
        public override string ToString() => $"{Name} (Id={Id})";
    }

    public class Retort
    {
        public int Id;
        public int ProcessorId;
        public string ProcessorName = "";
        public int Number;
        public int Capacity;
        public int Duration;
        public bool IsInProcess;
        public DateTime StartTime;
        public List<BasketInfo> Baskets = new();
        /// <summary>预计结束时间 = StartTime + Duration 分钟。仅当 IsInProcess 时有意义。</summary>
        public DateTime? EstimatedEndTime =>
            IsInProcess && StartTime > DateTime.MinValue && Duration > 0
                ? StartTime.AddMinutes(Duration) : null;
        public bool IsExpired =>
            IsInProcess && EstimatedEndTime != null && EstimatedEndTime.Value < DateTime.Now;
        public override string ToString() => $"Retort #{Number} (Id={Id}{(IsInProcess ? ", 进行中" : "")})";
    }

    public class BasketLookup
    {
        public int BasketId;
        public int Capacity;
        public int ProcessorId;
        public int RetortNumber;
        public bool IsEntryExists;
    }

    public class BasketInfo
    {
        public int BasketId;
        public int BasketNumber;
        public int Capacity;
        public int NumberOfUsedCassettes;
        public int ProcessorId;
        public int RetortNumber;
        public string HumanReadableId = "";
        public string BasketName = "";
        public List<Cassette> Cassettes = new();
        public string DisplayId =>
            !string.IsNullOrEmpty(HumanReadableId) ? HumanReadableId
            : !string.IsNullOrEmpty(BasketName) ? BasketName
            : BasketId.ToString();
        public override string ToString() =>
            $"{DisplayId}（容量 {Capacity}, 已用 {NumberOfUsedCassettes}）";
    }

    public class WorkItem
    {
        public long BlockId;
        public int CaseId;
        public string CassetteId = "";   // 用户可见 ID，如 "00011-2025-01-1"
        public string TissueName = "";
        public string GrossingLocation = "";
        public string GrossedUser = "";
        public override string ToString() =>
            string.IsNullOrEmpty(CassetteId) ? BlockId.ToString() : CassetteId;
    }

    public class Cassette
    {
        public long Id;
        public int CaseId;
        public string HumanReadableId = "";
        public string TissueType = "";
        public string Grosser = "";
        public override string ToString() =>
            string.IsNullOrEmpty(HumanReadableId) ? Id.ToString() : HumanReadableId;
    }

    public class WorkCell
    {
        public int WorkCellID;
        public string WorkcellName = "";
        public string WorkcellDesc = "";
        public int WorkcellTypeID;
        public string MACAddress = "";
        public override string ToString() => $"{WorkcellName} (Id={WorkCellID}, Type={WorkcellTypeID})";
    }

    public class Artifact
    {
        public int CaseId;
        public int SpecimenId;
        public int BlockId;
        public string SearchValue = "";
        public string ArtifactType = "";
        public string HumanReadableId = "";
        public bool IsCanceled;
    }
}
