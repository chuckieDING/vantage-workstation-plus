using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VantageWorkstationPlus.Services;

namespace VantageWorkstationPlus.ConfigTool
{
    public partial class MainWindow : Window
    {
        private readonly string _settingsPath;
        private JObject _rootJson = new();
        private readonly ObservableCollection<DbDataSource> _sources = new();
        private DbDataSource? _current;
        private bool _suppressUiSync;

        public MainWindow()
        {
            InitializeComponent();
            _settingsPath = ResolveAppSettingsPath();
            lblFilePath.Text = _settingsPath;
            lstSources.ItemsSource = _sources;
            LoadFromDisk();
        }

        /// <summary>优先用 EXE 同目录的 appsettings.json；没有就回退到主程序常见路径（向上 1 层）。</summary>
        private static string ResolveAppSettingsPath()
        {
            string local = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(local)) return local;
            string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "appsettings.json"));
            if (File.Exists(parent)) return parent;
            return local; // 不存在就用 local 路径，保存时创建
        }

        private void LoadFromDisk()
        {
            try
            {
                _rootJson = File.Exists(_settingsPath)
                    ? JObject.Parse(File.ReadAllText(_settingsPath))
                    : new JObject();
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取 appsettings.json 失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                _rootJson = new JObject();
            }
            _sources.Clear();
            if (_rootJson["DataSources"] is JArray arr)
            {
                foreach (var tok in arr)
                {
                    try
                    {
                        var ds = tok.ToObject<DbDataSource>();
                        if (ds != null) _sources.Add(ds);
                    }
                    catch { /* 单条解析失败跳过 */ }
                }
            }
            lblStatus.Text = $"已加载 {_sources.Count} 个数据源";
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var ds = new DbDataSource
            {
                Name = "DS-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                Provider = DbProvider.SqlServer,
                ConnectionString = "Server=;Database=;User Id=;Password=DPAPI:;TrustServerCertificate=true",
                Query = "SELECT id_column FROM your_table WHERE date_col='{today}'",
                IdColumn = "id_column",
                TargetPanel = "signoff",
                CronOrInterval = "",
            };
            _sources.Add(ds);
            lstSources.SelectedItem = ds;
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            if (MessageBox.Show($"确认删除「{_current.Name}」？", "删除确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _sources.Remove(_current);
            _current = null;
        }

        private void LstSources_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CommitDetailToCurrent();
            _current = lstSources.SelectedItem as DbDataSource;
            LoadCurrentToDetail();
            bool hasSel = _current != null;
            pnlDetail.IsEnabled = hasSel;
            btnDelete.IsEnabled = hasSel;
            btnTest.IsEnabled = hasSel;
            btnRunOnce.IsEnabled = hasSel;
        }

        private void LoadCurrentToDetail()
        {
            _suppressUiSync = true;
            try
            {
                if (_current == null)
                {
                    txtName.Text = txtConnStr.Text = txtQuery.Text = txtIdCol.Text =
                        txtAuxCols.Text = txtCron.Text = "";
                    cboProvider.SelectedIndex = -1;
                    cboTargetPanel.SelectedIndex = -1;
                    lblLastRun.Text = "—";
                    return;
                }
                txtName.Text = _current.Name;
                txtConnStr.Text = _current.ConnectionString;
                txtQuery.Text = _current.Query;
                txtIdCol.Text = _current.IdColumn;
                txtAuxCols.Text = string.Join(", ", _current.AuxColumns);
                txtCron.Text = _current.CronOrInterval ?? "";
                cboProvider.SelectedIndex = (int)_current.Provider;
                cboTargetPanel.SelectedItem = cboTargetPanel.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (string?)i.Tag == _current.TargetPanel);
                lblLastRun.Text = string.IsNullOrEmpty(_current.LastRun) ? "—" : _current.LastRun;
            }
            finally { _suppressUiSync = false; }
        }

        private void CommitDetailToCurrent()
        {
            if (_current == null || _suppressUiSync) return;
            _current.Name = txtName.Text.Trim();
            _current.ConnectionString = txtConnStr.Text.Trim();
            _current.Query = txtQuery.Text.Trim();
            _current.IdColumn = txtIdCol.Text.Trim();
            _current.AuxColumns = txtAuxCols.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            _current.CronOrInterval = txtCron.Text.Trim();
            if (cboProvider.SelectedItem is ComboBoxItem pi
                && Enum.TryParse<DbProvider>((string)pi.Content, out var prov))
                _current.Provider = prov;
            if (cboTargetPanel.SelectedItem is ComboBoxItem ti && ti.Tag is string tag)
                _current.TargetPanel = tag;
        }

        /// <summary>切换 provider 时，如果连接串为空或仍是别的 provider 的示例，自动填该 provider 的示例。</summary>
        private void CboProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUiSync || _current == null) return;
            if (cboProvider.SelectedItem is not ComboBoxItem item) return;
            if (!Enum.TryParse<DbProvider>((string)item.Content, out var prov)) return;
            string cur = txtConnStr.Text.Trim();
            // 只在为空或仍是已知示例时替换，避免覆盖用户已填的串
            bool isExample = string.IsNullOrEmpty(cur)
                || Enum.GetValues<DbProvider>().Any(p => cur == DbConnectionFactory.ExampleConnectionString(p));
            if (isExample) txtConnStr.Text = DbConnectionFactory.ExampleConnectionString(prov);
        }

        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            CommitDetailToCurrent();
            if (_current == null) return;
            try
            {
                string conn = ResolveConnString(_current.ConnectionString);
                var (ok, err) = DbConnectionFactory.TestConnection(_current.Provider, conn);
                MessageBox.Show(ok ? "✓ 连接成功" : "✗ 连接失败：\n" + err,
                    "测试连接", MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("✗ 测试异常：\n" + ex.Message, "测试连接",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRunOnce_Click(object sender, RoutedEventArgs e)
        {
            CommitDetailToCurrent();
            if (_current == null) return;
            try
            {
                var (ids, details) = DbExtractor.Extract(_current);
                if (ids.Count == 0)
                {
                    MessageBox.Show("查询成功但未返回任何行", "抽取一次",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                string file = DbExtractor.WriteToInbox(_current, details);
                _current.LastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                lblLastRun.Text = _current.LastRun;
                MessageBox.Show($"✓ 抽到 {ids.Count} 条，已写入：\n{file}", "抽取一次",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("✗ 抽取失败：\n" + ex.Message, "抽取一次",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEncrypt_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new EncryptDialog { Owner = this };
            dlg.ShowDialog();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            CommitDetailToCurrent();
            try
            {
                _rootJson["DataSources"] = JArray.FromObject(_sources.ToList());
                File.WriteAllText(_settingsPath, _rootJson.ToString(Formatting.Indented));
                lblStatus.Text = $"已保存 {_sources.Count} 个数据源到 {Path.GetFileName(_settingsPath)} · {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>把连接串里的 Password=DPAPI:xxx 解密成明文，用于测试连接。</summary>
        private static string ResolveConnString(string conn)
        {
            if (string.IsNullOrEmpty(conn)) return conn;
            var parts = conn.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq < 0) continue;
                string val = parts[i].Substring(eq + 1).Trim();
                if (val.StartsWith("DPAPI:", StringComparison.Ordinal))
                    parts[i] = parts[i].Substring(0, eq + 1) + SecretProtector.Decrypt(val);
            }
            return string.Join(";", parts);
        }
    }
}
