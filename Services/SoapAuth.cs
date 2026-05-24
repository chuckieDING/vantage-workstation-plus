using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace VantageWorkstationPlus.Services
{
    /// <summary>SOAP AuthHeader 生成器：复刻 Ventana.Vantage.Security.StringEncryption。
    /// 算法：AES-256-GCM；输出 = Base64( 12B 随机 nonce || ciphertext || 16B GCM tag )；
    /// 明文 = JSON({"Password":"&lt;NPLA_PASSWORD as base64 string&gt;"})；
    /// 32B AES key 来自用户级环境变量 {MAC}_NPLA_ENCRYPTION_KEY（Base64）。</summary>
    public class SoapAuth
    {
        public string MachineId { get; }
        private readonly byte[] _key;
        private readonly string _passwordPayload;
        public string ClientVersion { get; set; } = "4.1.25136.1";

        public SoapAuth(string mac, byte[] aesKey, string passwordB64)
        {
            MachineId = mac;
            _key = aesKey;
            _passwordPayload = passwordB64;
            if (_key.Length != 32)
                throw new ArgumentException($"AES key 必须 32 字节，实际 {_key.Length}");
        }

        /// <summary>从当前用户环境变量自动加载（与 WPF 客户端共用同一份注册）。</summary>
        public static SoapAuth FromEnvironment(string? overrideMac = null)
        {
            string mac = overrideMac ?? GetMacAddress();
            string? keyB64 = Environment.GetEnvironmentVariable($"{mac}_NPLA_ENCRYPTION_KEY", EnvironmentVariableTarget.User);
            string? pwdB64 = Environment.GetEnvironmentVariable($"{mac}_NPLA_PASSWORD", EnvironmentVariableTarget.User);
            if (string.IsNullOrEmpty(keyB64) || string.IsNullOrEmpty(pwdB64))
                throw new WorkstationNotRegisteredException(mac);
            return new SoapAuth(mac, Convert.FromBase64String(keyB64), pwdB64);
        }

        /// <summary>已注册（环境变量存在）。</summary>
        public static bool IsRegistered(string? mac = null)
        {
            string m = mac ?? GetMacAddress();
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable($"{m}_NPLA_ENCRYPTION_KEY", EnvironmentVariableTarget.User))
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable($"{m}_NPLA_PASSWORD", EnvironmentVariableTarget.User));
        }

        /// <summary>计算 V3ntanaNPLAD3faultEncryptionK3y + serverIp 的 SHA256 派生密钥（用于首次握手解密）。</summary>
        public static byte[] ComputeKnownEncryptionKey(string serverIpOrHost)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes("V3ntanaNPLAD3faultEncryptionK3y" + serverIpOrHost));
        }

        /// <summary>用任意 32B 密钥 AES-GCM 加密任意明文（与 StringEncryption.Encrypt(string, byte[]) 等价）。</summary>
        public static string EncryptWithKey(string plaintext, byte[] key)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] pt = Encoding.UTF8.GetBytes(plaintext);
            byte[] ct = new byte[pt.Length];
            byte[] tag = new byte[16];
            using var gcm = new AesGcm(key, 16);
            gcm.Encrypt(nonce, pt, ct, tag);
            return Convert.ToBase64String(nonce.Concat(ct).Concat(tag).ToArray());
        }

        /// <summary>新建一对 32B 随机 key/password（Base64），写入用户环境变量并返回 SoapAuth + 同样的 key/password Base64
        /// （调用方用它们构造发给服务器的 SecurityHandshakePayloadModel JSON）。</summary>
        public static (SoapAuth Auth, string KeyB64, string PasswordB64) GenerateAndStoreLocally(string? mac = null)
        {
            string m = mac ?? GetMacAddress();
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] pwd = RandomNumberGenerator.GetBytes(32);
            string keyB64 = Convert.ToBase64String(key);
            string pwdB64 = Convert.ToBase64String(pwd);
            Environment.SetEnvironmentVariable($"{m}_NPLA_ENCRYPTION_KEY", keyB64, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable($"{m}_NPLA_PASSWORD", pwdB64, EnvironmentVariableTarget.User);
            return (new SoapAuth(m, key, pwdB64), keyB64, pwdB64);
        }

        /// <summary>SaveStation.macAddress 字段：新随机 GUID 前 25 字符（与 WPF ClientSetup.LoadMachineIds 一致）。
        /// 同时把生成的 MachId 写到环境变量持久化，方便排错和重启后重用。</summary>
        public static string GenerateMachId(string? mac = null)
        {
            string m = mac ?? GetMacAddress();
            string id = Guid.NewGuid().ToString().Substring(0, 25);
            Environment.SetEnvironmentVariable($"{m}_NPLA_MACH_ID", id, EnvironmentVariableTarget.User);
            return id;
        }

        public static string? GetSavedMachId(string? mac = null)
        {
            string m = mac ?? GetMacAddress();
            return Environment.GetEnvironmentVariable($"{m}_NPLA_MACH_ID", EnvironmentVariableTarget.User);
        }

        /// <summary>读 / 写本工作站在服务器上的 WorkCellId（用 {MAC}_NPLA_WORKCELL_ID 持久化）。</summary>
        public static int? GetSavedWorkCellId(string? mac = null)
        {
            string m = mac ?? GetMacAddress();
            string? s = Environment.GetEnvironmentVariable($"{m}_NPLA_WORKCELL_ID", EnvironmentVariableTarget.User);
            return int.TryParse(s, out int v) ? v : (int?)null;
        }

        public static void SaveWorkCellId(int workCellId, string? mac = null)
        {
            string m = mac ?? GetMacAddress();
            Environment.SetEnvironmentVariable($"{m}_NPLA_WORKCELL_ID",
                workCellId.ToString(), EnvironmentVariableTarget.User);
        }

        public static void ClearEnvironment(string? mac = null)
        {
            string m = mac ?? GetMacAddress();
            Environment.SetEnvironmentVariable($"{m}_NPLA_ENCRYPTION_KEY", null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable($"{m}_NPLA_PASSWORD", null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable($"{m}_NPLA_WORKCELL_ID", null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable($"{m}_NPLA_MACH_ID", null, EnvironmentVariableTarget.User);
        }

        /// <summary>每次新 nonce → 每次返回不同 blob。Replays 系统不接受。</summary>
        public string GeneratePassword()
        {
            string plaintext = JsonConvert.SerializeObject(new { Password = _passwordPayload });
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] pt = Encoding.UTF8.GetBytes(plaintext);
            byte[] ct = new byte[pt.Length];
            byte[] tag = new byte[16];
            using var gcm = new AesGcm(_key, 16);
            gcm.Encrypt(nonce, pt, ct, tag);
            return Convert.ToBase64String(nonce.Concat(ct).Concat(tag).ToArray());
        }

        /// <summary>构造 SOAP &lt;soap:Header&gt; 内的 AuthHeader 节点（带 XML 转义）。</summary>
        public string BuildAuthHeaderXml()
        {
            return
                "<AuthHeader xmlns=\"http://tempuri.org/\">" +
                $"<Username>{System.Security.SecurityElement.Escape(MachineId)}</Username>" +
                $"<Password>{System.Security.SecurityElement.Escape(GeneratePassword())}</Password>" +
                $"<ClientVersion>{System.Security.SecurityElement.Escape(ClientVersion)}</ClientVersion>" +
                "</AuthHeader>";
        }

        /// <summary>从 BaseUrl 提取 host（用于 ComputeKnownEncryptionKey）。</summary>
        public static string ExtractHost(string baseUrl)
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var u)) return u.Host;
            return baseUrl;
        }

        /// <summary>跟 Ventana 原版 Utility.GetMacAddress() 完全一致：所有 Up 网卡取第一个，
        /// 不过滤类型——否则跟 WPF 选不同的卡，env var 名字就对不上读不到。</summary>
        public static string GetMacAddress()
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .FirstOrDefault();
            if (nic == null) throw new InvalidOperationException("找不到可用网卡来获取 MAC");
            return nic.GetPhysicalAddress().ToString();  // 默认大写无分隔符
        }

        /// <summary>列出所有 Up 网卡的 MAC 和检测到的 NPLA env var，用于排查注册状态。</summary>
        public static string DiagnoseNics()
        {
            var sb = new StringBuilder();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                string mac = nic.GetPhysicalAddress().ToString();
                bool hasKey = !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable($"{mac}_NPLA_ENCRYPTION_KEY", EnvironmentVariableTarget.User));
                sb.AppendLine($"  [{nic.NetworkInterfaceType}] {nic.Name}  MAC={mac}" +
                    (hasKey ? "  ← 这张卡有 NPLA_ENCRYPTION_KEY env var" : ""));
            }
            return sb.ToString();
        }
    }

    public class WorkstationNotRegisteredException : Exception
    {
        public string Mac { get; }
        public WorkstationNotRegisteredException(string mac)
            : base($"工作站未注册（环境变量 {mac}_NPLA_ENCRYPTION_KEY/_PASSWORD 不存在）") { Mac = mac; }
    }
}
