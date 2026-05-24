using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VantageWorkstationPlus.Services;

namespace VantageWorkstationPlus.Panels
{
    public partial class ArchivePanel : UserControl
    {
        private List<string> _objects = new();
        private ArchiveSession? _archive;
        private bool _initialized;

        public ArchivePanel()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (_initialized) return;
                _initialized = true;
                await InitArchiveAsync();
            };
        }

        private async Task InitArchiveAsync()
        {
            if (App.Session == null) return;
            try
            {
                _archive = new ArchiveSession(App.Session);
                await _archive.InitializeAsync();
                Log("已初始化归档会话；位置列表将在第一次加载病例后填充");
            }
            catch (Exception ex)
            {
                Log("[错误] 初始化归档页面失败: " + ex.Message);
            }
        }

        private void BtnDownloadTemplate_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Excel|*.xlsx",
                FileName = $"archive-template-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var cols = new List<ExcelTemplateWriter.Column>
                {
                    new("对象 ID（蜡块/玻片）", "00015-2025-3-1"),
                    new("类型", "Block"),
                    new("当前位置", ""),
                    new("备注", ""),
                };
                // 预填 NotArchived 列表（如已加载过）
                var rows = (_archive?.NotArchived ?? new List<ArtifactRow>())
                    .Select(r => (IReadOnlyList<string>)new[]
                    {
                        r.ArtifactText, r.ArtifactType, r.ArtifactLocation, "",
                    }).ToList();
                ExcelTemplateWriter.Write(dlg.FileName, "归档对象", cols, rows);
                Log($"模板已导出: {dlg.FileName}（预填 {rows.Count} 个未归档对象）");
            }
            catch (Exception ex)
            {
                MessageBox.Show("生成模板失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
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
                return;
            }

            // 自动用第一个 ID 探一次，把归档位置下拉拉出来
            if (_archive != null && _objects.Count > 0 && cboLocation.Items.Count == 0)
            {
                Log($"用 {_objects[0]} 预加载归档位置...");
                try
                {
                    await _archive.LoadCaseAsync(_objects[0]);
                    cboLocation.Items.Clear();
                    foreach (var (id, name) in _archive.Locations)
                        cboLocation.Items.Add(new ComboBoxItem { Content = $"{name}  [{id}]", Tag = id });
                    Log($"已加载 {_archive.Locations.Count} 个归档位置");
                    if (cboLocation.Items.Count > 0) cboLocation.SelectedIndex = 0;
                }
                catch (Exception ex)
                {
                    Log("[警告] 预加载位置失败: " + ex.Message);
                }
            }
        }

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_archive == null) { Log("[错误] 归档会话未初始化"); return; }
            if (_objects.Count == 0)
            {
                MessageBox.Show("请先选择对象编号文件", "无数据", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 兜底：万一选文件时没拉到位置，运行时再试一次
            if (cboLocation.Items.Count == 0)
            {
                Log($"位置下拉为空，重新拉一次...");
                try
                {
                    await _archive.LoadCaseAsync(_objects[0]);
                    cboLocation.Items.Clear();
                    foreach (var (id, name) in _archive.Locations)
                        cboLocation.Items.Add(new ComboBoxItem { Content = $"{name}  [{id}]", Tag = id });
                    Log($"已加载 {_archive.Locations.Count} 个归档位置");
                    if (cboLocation.Items.Count > 0) cboLocation.SelectedIndex = 0;
                    if (cboLocation.Items.Count == 0)
                    {
                        MessageBox.Show("无法加载归档位置，请检查日志", "失败");
                        return;
                    }
                    MessageBox.Show("请选择归档位置后再次点击", "请选择位置",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                catch (Exception ex)
                {
                    Log("[错误] 加载位置失败: " + ex.Message);
                    return;
                }
            }

            int? locId = (cboLocation.SelectedItem as ComboBoxItem)?.Tag as int?;
            if (locId == null)
            {
                MessageBox.Show("请选择归档位置", "缺位置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnRun.IsEnabled = false;
            try
            {
                progress.Maximum = _objects.Count;
                progress.Value = 0;

                // 按 case 分组：从 ID 提取前两段（如 "00015-2025-01-3-1" → "00015-2025"）
                var groups = _objects
                    .GroupBy(id => GetCaseKey(id))
                    .ToList();

                int ok = 0, fail = 0;
                Log($"开始归档 {_objects.Count} 个对象到 LocationId={locId}（按 {groups.Count} 个 case 分组）");

                foreach (var grp in groups)
                {
                    string sample = grp.First();
                    Log($"--- Case {grp.Key}（{grp.Count()} 个对象，使用 {sample} 加载）---");
                    try
                    {
                        await _archive.LoadCaseAsync(sample);
                    }
                    catch (Exception ex)
                    {
                        Log($"  [错误] 加载 case 失败: {ex.Message}");
                        fail += grp.Count();
                        progress.Value += grp.Count();
                        continue;
                    }

                    // 用 NotArchived 列表查匹配（输入是 LisSlideId 格式，网格用 ExtSlideId 格式）
                    foreach (var target in grp)
                    {
                        var row = _archive.NotArchived.FirstOrDefault(r => MatchesArtifact(r, target));
                        if (row == null)
                        {
                            // 已经归档过？
                            var arch = _archive.Archived.FirstOrDefault(r => MatchesArtifact(r, target));
                            if (arch != null)
                            {
                                Log($"  SKIP {target}: 已在归档（位置 {arch.ArtifactLocation}）");
                            }
                            else
                            {
                                Log($"  FAIL {target}: 未在该 case 找到");
                                fail++;
                            }
                            progress.Value++;
                            continue;
                        }
                        var err = await _archive.MarkForArchiveAsync(row, locId.Value, sample);
                        if (!string.IsNullOrEmpty(err))
                        {
                            Log($"  FAIL {target}: {err}");
                            fail++;
                        }
                        else
                        {
                            Log($"  标记 {target} → 归档队列");
                            ok++;
                        }
                        progress.Value++;
                    }

                    // 该 case 全部标记完，提交一次
                    var saveErr = await _archive.SaveAsync(sample, locId.Value);
                    if (!string.IsNullOrEmpty(saveErr))
                        Log($"  [错误] 保存失败: {saveErr}");
                    else
                        Log($"  ✓ Case {grp.Key} 提交成功");
                }
                Log($"=== 完成: 成功 {ok} 个, 失败 {fail} 个 ===");
            }
            catch (Exception ex)
            {
                Log("[错误] 批量归档异常: " + ex.Message);
            }
            finally
            {
                btnRun.IsEnabled = true;
                progress.Value = 0;
            }
        }

        private static string GetCaseKey(string artifactId)
        {
            // "00015-2025-01-3-1" → "00015-2025"
            // "00015-2025-3-1"    → "00015-2025"
            var parts = artifactId.Split('-');
            return parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : artifactId;
        }

        /// <summary>匹配输入与网格行：兼容 LisSlideId（4 段） vs ExtSlideId（5 段，多 "标本号" 段）。</summary>
        private static bool MatchesArtifact(ArtifactRow row, string input)
        {
            if (row.ArtifactText == input || row.LISSpecID == input) return true;
            // 尝试把 row 的 ExtSlideId 形式去掉第 3 段（标本号），变 LisSlideId 形式
            var rowParts = row.ArtifactText.Split('-');
            if (rowParts.Length >= 3)
            {
                var lisForm = string.Join("-",
                    new[] { rowParts[0], rowParts[1] }.Concat(rowParts.Skip(3)));
                if (lisForm == input) return true;
            }
            // 反向：input 是 ExtSlideId 形式，row 是 LisSlideId 形式（一般不会发生但兜底）
            var inputParts = input.Split('-');
            if (inputParts.Length >= 3)
            {
                var inputLisForm = string.Join("-",
                    new[] { inputParts[0], inputParts[1] }.Concat(inputParts.Skip(3)));
                if (row.ArtifactText == inputLisForm) return true;
            }
            return false;
        }

        private void Log(string msg) => BatchPanelBase.Log(logBox, logScroll, nameof(ArchivePanel), msg);
    }
}
