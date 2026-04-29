using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VantageWorkstationPlus.Models;
using VantageWorkstationPlus.Services;

namespace VantageWorkstationPlus.Panels
{
    public partial class SignOffPanel : UserControl
    {
        private readonly Dictionary<string, (Location Loc, List<Pathologist> Paths)> _locCache = new();
        private List<string> _slides = new();
        private bool _initialized;

        public SignOffPanel()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (_initialized) return;
                _initialized = true;
                await LoadLocationsAsync();
                await ReloadPathologistsAsync();
            };
        }

        private async Task LoadLocationsAsync()
        {
            cboLocation.Items.Clear();
            cboLocation.Items.Add(new ComboBoxItem { Content = "(无 - 显示全部医生)", Tag = null });
            try
            {
                var list = await FoldersApi.GetLocationsAsync(App.Session!);
                foreach (var (barcode, name) in list)
                    cboLocation.Items.Add(new ComboBoxItem { Content = $"{name}  [{barcode}]", Tag = barcode });
                Log($"已加载 {list.Count} 个出片位置");
            }
            catch (Exception ex)
            {
                Log("[警告] 加载位置失败: " + ex.Message);
            }
            cboLocation.SelectedIndex = 0;
        }

        private async void CboLocation_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            await ReloadPathologistsAsync();
        }

        private async Task ReloadPathologistsAsync()
        {
            FillPathologists(new List<Pathologist>(), null);
            string? barcode = (cboLocation.SelectedItem as ComboBoxItem)?.Tag as string;
            try
            {
                if (string.IsNullOrEmpty(barcode))
                {
                    var paths = await FoldersApi.GetAllPathologistsAsync(App.Session!);
                    FillPathologists(paths, null);
                }
                else
                {
                    if (_locCache.TryGetValue(barcode, out var cached))
                    {
                        FillPathologists(cached.Paths, cached.Loc);
                        return;
                    }
                    var (loc, paths, err) = await FoldersApi.ScanLocationAsync(App.Session!, barcode);
                    if (err != null) { Log("[警告] 扫位置失败: " + err); return; }
                    _locCache[barcode] = (loc!, paths);
                    FillPathologists(paths, loc);
                }
            }
            catch (Exception ex) { Log("[警告] 加载医生失败: " + ex.Message); }
        }

        private void FillPathologists(List<Pathologist> paths, Location? loc)
        {
            cboPathologist.Items.Clear();
            cboPathologist.Items.Add(new ComboBoxItem { Content = "(不指定)", Tag = (int?)null });
            foreach (var p in paths)
                cboPathologist.Items.Add(new ComboBoxItem { Content = $"{p.FullName}  [{p.PathologistCode}]", Tag = (int?)p.Id });
            cboPathologist.SelectedIndex = 0;
            if (paths.Count > 0)
                Log(loc == null ? $"未选位置: 加载全部 {paths.Count} 位医生" : $"位置 {loc.LocationNm}: {paths.Count} 位关联医生");
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            string? path = BatchPanelBase.PickFile("选择玻片号文件");
            if (path == null) return;
            try
            {
                _slides = SlideListReader.Read(path);
                txtFile.Text = path;
                lstSlides.Items.Clear();
                foreach (var sid in _slides) lstSlides.Items.Add(sid);
                Log($"读取到 {_slides.Count} 张玻片号");
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_slides.Count == 0)
            {
                MessageBox.Show("请先选择玻片号文件", "无数据", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string? barcode = (cboLocation.SelectedItem as ComboBoxItem)?.Tag as string;
            int? pathIdNullable = (cboPathologist.SelectedItem as ComboBoxItem)?.Tag as int?;
            int locId = -1;
            int pathId = pathIdNullable ?? -1;

            btnRun.IsEnabled = false;
            try
            {
                if (!string.IsNullOrEmpty(barcode))
                {
                    var (loc, _, err) = await FoldersApi.ScanLocationAsync(App.Session!, barcode);
                    if (err != null) { MessageBox.Show(err, "位置无效"); return; }
                    locId = loc!.LocationId;
                    Log($"位置 {barcode} → LocationId={locId} ({loc.LocationNm})");
                }
                else Log("未指定出片位置（locId=-1）");
                if (pathId == -1) Log("未指定病理医生（pathId=-1）");

                progress.Maximum = _slides.Count;
                progress.Value = 0;

                var successful = new List<(string Slide, string Ts)>();
                var failed = new List<(string Slide, string Reason)>();
                Log($"开始处理 {_slides.Count} 张玻片...");
                foreach (var sid in _slides)
                {
                    var r = await FoldersApi.ScanSlideAsync(App.Session!, sid);
                    if (r.Ok)
                    {
                        successful.Add((r.LisSlideId, r.InsertTs));
                        Log($"  OK {sid} → {r.LisSlideId} ts={r.InsertTs}");
                    }
                    else
                    {
                        failed.Add((sid, r.Error));
                        Log($"  FAIL {sid}: {r.Error}");
                    }
                    progress.Value++;
                    await Task.Delay(200);
                }

                if (successful.Count == 0) { Log("=== 全部玻片扫描失败 ==="); return; }

                var (data, signErr) = await FoldersApi.SaveAndSignOffAsync(App.Session!,
                    successful.Select(x => x.Slide).ToList(),
                    successful.Select(x => x.Ts).ToList(),
                    locId, pathId);
                if (signErr != null) Log($"  签发失败: {signErr}");
                else Log($"  ✓ 签发成功 {successful.Count} 张 (locId={locId} pathId={pathId})");
                Log($"=== 完成: 成功 {successful.Count}, 失败 {failed.Count} ===");
            }
            catch (Exception ex)
            {
                Log("[错误] 批量执行异常: " + ex.Message);
            }
            finally
            {
                btnRun.IsEnabled = true;
                progress.Value = 0;
            }
        }

        private void Log(string msg) => BatchPanelBase.Log(logBox, logScroll, nameof(SignOffPanel), msg);
    }
}
