using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json.Linq;

namespace VantageWorkstationPlus.Dialogs
{
    public partial class SettingsDialog : Window
    {
        private readonly string _settingsPath;
        private JObject _json;

        public SettingsDialog()
        {
            InitializeComponent();
            _settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            try
            {
                _json = File.Exists(_settingsPath)
                    ? JObject.Parse(File.ReadAllText(_settingsPath))
                    : new JObject();
            }
            catch
            {
                _json = new JObject();
            }
            LoadFromJson();
        }

        private void LoadFromJson()
        {
            txtBaseUrl.Text = _json.Value<string>("BaseUrl") ?? App.BaseUrl;
            txtWorkCellId.Text = (_json.Value<int?>("WorkCellId") ?? App.WorkCellId).ToString();
            chkAcceptAnyCert.IsChecked = _json.Value<bool?>("AcceptAnyServerCert") ?? App.AcceptAnyServerCert;
            var tabs = (_json["EnabledTabs"] as JArray)?
                .Select(t => t.ToString().ToLowerInvariant()).ToHashSet()
                ?? new System.Collections.Generic.HashSet<string> { "signoff", "dehydration", "archive", "transfer" };
            chkSignoff.IsChecked = tabs.Contains("signoff");
            chkDehydration.IsChecked = tabs.Contains("dehydration");
            chkArchive.IsChecked = tabs.Contains("archive");
            chkTransfer.IsChecked = tabs.Contains("transfer");
        }

        private void Title_DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtWorkCellId.Text.Trim(), out int wcId) || wcId < 0)
            {
                MessageBox.Show("WorkCellId 必须是非负整数", "无效输入",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _json["BaseUrl"] = txtBaseUrl.Text.Trim();
            _json["WorkCellId"] = wcId;
            _json["AcceptAnyServerCert"] = chkAcceptAnyCert.IsChecked == true;
            var enabled = new JArray();
            if (chkSignoff.IsChecked == true) enabled.Add("signoff");
            if (chkDehydration.IsChecked == true) enabled.Add("dehydration");
            if (chkArchive.IsChecked == true) enabled.Add("archive");
            if (chkTransfer.IsChecked == true) enabled.Add("transfer");
            _json["EnabledTabs"] = enabled;

            try
            {
                File.WriteAllText(_settingsPath, _json.ToString(Newtonsoft.Json.Formatting.Indented));
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
