using System.Configuration;

namespace VantageWorkstationPlus.Properties
{
    /// <summary>极简用户设置（保存上次登录的服务器地址）。</summary>
    internal sealed class Settings : ApplicationSettingsBase
    {
        public static Settings Default { get; } = (Settings)Synchronized(new Settings());

        [UserScopedSetting]
        [DefaultSettingValue("http://192.168.127.128")]
        public string BaseUrl
        {
            get => (string)this["BaseUrl"];
            set => this["BaseUrl"] = value;
        }
    }
}
