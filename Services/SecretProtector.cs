using System;
using System.Security.Cryptography;
using System.Text;

namespace VantageWorkstationPlus.Services
{
    /// <summary>Windows DPAPI CurrentUser scope 加解密：用来保存数据源密码到 appsettings.json。
    /// 加密后的 base64 只在加密时的同一台机 + 同一 Windows 用户下能解密。</summary>
    public static class SecretProtector
    {
        public static string Encrypt(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] data = Encoding.UTF8.GetBytes(plain);
            byte[] cipher = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return "DPAPI:" + Convert.ToBase64String(cipher);
        }

        public static string Decrypt(string encryptedOrPlain)
        {
            if (string.IsNullOrEmpty(encryptedOrPlain)) return "";
            // 兼容明文密码：没有 DPAPI: 前缀就当原样返回
            if (!encryptedOrPlain.StartsWith("DPAPI:")) return encryptedOrPlain;
            byte[] cipher = Convert.FromBase64String(encryptedOrPlain.Substring("DPAPI:".Length));
            byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
    }
}
