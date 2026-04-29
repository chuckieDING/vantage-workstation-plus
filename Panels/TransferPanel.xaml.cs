using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VantageWorkstationPlus.Services;

namespace VantageWorkstationPlus.Panels
{
    public partial class TransferPanel : UserControl
    {
        private List<string> _objects = new();
        private TrackingSession? _tracking;
        private bool _initialized;

        public TransferPanel()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (_initialized) return;
                _initialized = true;
                await InitTrackingAsync();
            };
        }

        private async Task InitTrackingAsync()
        {
            if (App.Session == null) return;
            try
            {
                _tracking = new TrackingSession(App.Session);
                await _tracking.InitializeAsync();
                cboLocation.Items.Clear();
                foreach (var (id, name) in _tracking.Locations)
                    cboLocation.Items.Add(new ComboBoxItem { Content = $"{name}  [{id}]", Tag = id });
                Log($"已加载 {_tracking.Locations.Count} 个流转位置");
                if (cboLocation.Items.Count > 0) cboLocation.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Log("[错误] 初始化流转页面失败: " + ex.Message);
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            string? path = BatchPanelBase.PickFile("选择对象编号文件");
            if (path == null) return;
            try
            {
                _objects = SlideListReader.Read(path);
                txtFile.Text = path;
                lstObjects.Items.Clear();
                foreach (var sid in _objects) lstObjects.Items.Add(sid);
                Log($"读取到 {_objects.Count} 个对象编号");
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnDry_Click(object sender, RoutedEventArgs e) => await RunAsync(true);
        private async void BtnRun_Click(object sender, RoutedEventArgs e) => await RunAsync(false);

        private async Task RunAsync(bool dryRun)
        {
            if (_objects.Count == 0)
            {
                MessageBox.Show("请先选择对象编号文件", "无数据", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_tracking == null) { Log("[错误] 流转会话未初始化"); return; }
            int? locId = (cboLocation.SelectedItem as ComboBoxItem)?.Tag as int?;
            if (locId == null)
            {
                MessageBox.Show("请选择流转位置", "缺位置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (dryRun)
            {
                Log($"[DRY-RUN] 将向位置 {locId} 流转 {_objects.Count} 个对象（不实际请求）");
                foreach (var o in _objects) Log("  " + o);
                return;
            }

            btnRun.IsEnabled = false;
            try
            {
                progress.Maximum = _objects.Count;
                progress.Value = 0;
                int ok = 0, fail = 0;
                Log($"开始流转 {_objects.Count} 个对象到 LocationId={locId}...");
                foreach (var sid in _objects)
                {
                    var r = await _tracking.TrackAsync(locId.Value, sid);
                    if (r.Ok)
                    {
                        ok++;
                        Log($"  OK  {sid}");
                    }
                    else
                    {
                        fail++;
                        Log($"  FAIL {sid}: {r.Error}");
                    }
                    progress.Value++;
                    await Task.Delay(200);
                }
                Log($"=== 完成: 成功 {ok} 个, 失败 {fail} 个 ===");
            }
            catch (Exception ex)
            {
                Log("[错误] 批量流转异常: " + ex.Message);
            }
            finally
            {
                btnRun.IsEnabled = true;
                progress.Value = 0;
            }
        }

        private void Log(string msg) => BatchPanelBase.Log(logBox, logScroll, nameof(TransferPanel), msg);
    }
}
