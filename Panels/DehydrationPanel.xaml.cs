using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using VantageWorkstationPlus.Services;

namespace VantageWorkstationPlus.Panels
{
    public partial class DehydrationPanel : UserControl
    {
        private TouchScreenSession? _ts;
        private List<TissueProcessor> _processors = new();
        private List<string> _objects = new();         // 文件中待添加的蜡块 ID
        private List<WorkItem> _basketItems = new();    // 当前篮内蜡块（来自服务端）
        private BasketInfo? _currentBasket;
        private bool _initialized;
        private DispatcherTimer? _statusTimer;
        private bool _refreshing;
        private System.Threading.CancellationTokenSource? _batchCts;
        // 已经提示过"已超时"的 RetortId，避免每 10s 弹一次
        private readonly HashSet<int> _expiredPrompted = new();
        // 上次渲染的状态/篮指纹，相同则跳过重建避免抖动
        private string? _lastStatusSig;
        private string? _lastBasketSig;

        public DehydrationPanel()
        {
            InitializeComponent();
            dpEndDate.SelectedDate = DateTime.Today.AddDays(1);
            Loaded += async (_, _) =>
            {
                if (!_initialized) { _initialized = true; await InitAsync(); }
                StartStatusTimer();
            };
            Unloaded += (_, _) => StopStatusTimer();
        }

        private void StartStatusTimer()
        {
            if (_statusTimer != null) return;
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _statusTimer.Tick += async (_, _) => await TimerRefreshAsync();
            _statusTimer.Start();
        }

        private void StopStatusTimer()
        {
            _statusTimer?.Stop();
            _statusTimer = null;
        }

        private async Task TimerRefreshAsync()
        {
            if (_refreshing || _ts == null) return;
            if ((cboProcessor.SelectedItem as ComboBoxItem)?.Tag is not TissueProcessor p) return;
            await RefreshProcessorStatusAsync(p, promptExpired: true);
        }

        private async Task InitAsync()
        {
            if (App.SoapSession == null)
            {
                Log("[警告] 未建立 SOAP 会话；请重新登录");
                btnAddCassettes.IsEnabled = btnStart.IsEnabled = btnEnd.IsEnabled = false;
                return;
            }
            _ts = App.SoapSession;
            await LoadDevicesAsync();
        }

        private async Task LoadDevicesAsync()
        {
            if (_ts == null) return;

            // 记住当前选中的 ID，刷新后尽量保留
            int? prevProcessorId = ((cboProcessor.SelectedItem as ComboBoxItem)?.Tag as TissueProcessor)?.Id;
            int? prevBasketId = ((cboBasket.SelectedItem as ComboBoxItem)?.Tag as BasketInfo)?.BasketId;

            try
            {
                _processors = await _ts.GetTissueProcessorsAsync();
                cboProcessor.Items.Clear();
                foreach (var p in _processors.Where(p => p.IsActive))
                    cboProcessor.Items.Add(new ComboBoxItem { Content = p.ToString(), Tag = p });
                Log($"已加载 {cboProcessor.Items.Count} 台脱水机");
                int restoreIdx = -1;
                if (prevProcessorId != null)
                {
                    for (int i = 0; i < cboProcessor.Items.Count; i++)
                        if (((cboProcessor.Items[i] as ComboBoxItem)?.Tag as TissueProcessor)?.Id == prevProcessorId)
                        { restoreIdx = i; break; }
                }
                cboProcessor.SelectedIndex = restoreIdx >= 0 ? restoreIdx
                    : (cboProcessor.Items.Count > 0 ? 0 : -1);
            }
            catch (Exception ex)
            {
                Log("[错误] 加载脱水机失败: " + ex.Message);
            }
            try
            {
                var baskets = await _ts.GetBasketForTPListAsync();
                cboBasket.Items.Clear();
                foreach (var b in baskets)
                    cboBasket.Items.Add(new ComboBoxItem { Content = b.ToString(), Tag = b });
                Log($"已加载 {baskets.Count} 个脱水框");
                int restoreIdx = -1;
                if (prevBasketId != null)
                {
                    for (int i = 0; i < cboBasket.Items.Count; i++)
                        if (((cboBasket.Items[i] as ComboBoxItem)?.Tag as BasketInfo)?.BasketId == prevBasketId)
                        { restoreIdx = i; break; }
                }
                cboBasket.SelectedIndex = restoreIdx >= 0 ? restoreIdx
                    : (cboBasket.Items.Count > 0 ? 0 : -1);
            }
            catch (Exception ex)
            {
                Log("[错误] 加载脱水框失败: " + ex.Message);
            }
        }

        private async void CboProcessor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            cboRetort.Items.Clear();
            if ((cboProcessor.SelectedItem as ComboBoxItem)?.Tag is not TissueProcessor p) return;
            foreach (var r in p.Retorts)
                cboRetort.Items.Add(new ComboBoxItem { Content = r.ToString(), Tag = r });
            if (cboRetort.Items.Count > 0) cboRetort.SelectedIndex = 0;

            _expiredPrompted.Clear();  // 切换脱水机时重置提示状态
            await RefreshProcessorStatusAsync(p, promptExpired: true);
        }

        /// <summary>渲染所选脱水机的层级状态：缸 → 篮 → 蜡块。
        /// 用 _lastStatusSig 做内容指纹，没变化只刷新右上"最近刷新"时间戳，避免 10s 重建闪烁。</summary>
        private async Task RefreshProcessorStatusAsync(TissueProcessor p, bool promptExpired)
        {
            if (_ts == null || _refreshing) return;
            _refreshing = true;
            try
            {
                // 拉数据；瞬时网络错误（HttpRequestException）重试一次再放过
                var retorts = new List<Retort>();
                foreach (var r0 in p.Retorts)
                {
                    Retort? r = null;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        try { r = await _ts.GetRetortInfoAsync(p.Id, r0.Number); break; }
                        catch (System.Net.Http.HttpRequestException) when (attempt == 0)
                        { await Task.Delay(500); }
                        catch (Exception ex)
                        {
                            Log($"[警告] GetRetortInfo({p.Id},{r0.Number}): {ex.Message}");
                            break;
                        }
                    }
                    retorts.Add(r ?? r0);
                }

                // 计算指纹（只看显示相关字段，时间戳不计入）
                string sig = ComputeStatusSignature(p, retorts);
                lblProcessorStatus.Text = $"{p.Name}（共 {p.NumberOfRetorts} 缸 / {p.NumberOfBaskets} 篮）" +
                    $"  · 最近刷新 {DateTime.Now:HH:mm:ss}";

                if (sig != _lastStatusSig)
                {
                    _lastStatusSig = sig;
                    treeStatus.Items.Clear();
                    foreach (var r in retorts) treeStatus.Items.Add(BuildRetortNode(r));
                }

                if (promptExpired)
                {
                    var newlyExpired = retorts.Where(r => r.IsExpired && !_expiredPrompted.Contains(r.Id)).ToList();
                    if (newlyExpired.Count > 0)
                    {
                        var first = newlyExpired.First();
                        _expiredPrompted.Add(first.Id);
                        var ans = MessageBox.Show(
                            $"检测到脱水缸 #{first.Number}（ID {first.Id}）已超过预计结束时间但仍处于运行状态。\n\n" +
                            "是否立即结束（标蜡块为已处理 + 卸下篮中蜡块）？",
                            "脱水已到时", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (ans == MessageBoxResult.Yes)
                        {
                            await EndExpiredRetortAsync(first);
                            _refreshing = false;
                            _lastStatusSig = null;  // 状态已变，强制刷新一次
                            await RefreshProcessorStatusAsync(p, promptExpired: false);
                            return;
                        }
                    }
                }
            }
            finally { _refreshing = false; }
        }

        private static string ComputeStatusSignature(TissueProcessor p, List<Retort> retorts)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(p.Id).Append('|');
            foreach (var r in retorts)
            {
                sb.Append(r.Id).Append(':').Append(r.IsInProcess ? '1' : '0').Append(':')
                  .Append(r.Duration).Append(':')
                  .Append(r.StartTime.Ticks).Append(':')
                  .Append(r.IsExpired ? 'E' : '_').Append(':');
                foreach (var b in r.Baskets)
                {
                    sb.Append(b.BasketId).Append('/').Append(b.NumberOfUsedCassettes).Append(',');
                    foreach (var c in b.Cassettes) sb.Append(c.Id).Append(';');
                }
                sb.Append('|');
            }
            return sb.ToString();
        }

        private TreeViewItem BuildRetortNode(Retort r)
        {
            var node = new TreeViewItem { IsExpanded = true };
            string title = $"Retort #{r.Number} (ID {r.Id})";
            if (r.IsInProcess)
            {
                var end = r.EstimatedEndTime;
                string suffix = end != null
                    ? (r.IsExpired
                        ? $" · 进行中 · ⚠ 已超时（预计 {end:yyyy-MM-dd HH:mm}）"
                        : $" · 进行中 · 预计结束 {end:yyyy-MM-dd HH:mm}")
                    : " · 进行中";
                title += suffix;
            }
            else title += " · 空闲";
            node.Header = MakeStatusHeader(title, r.IsExpired);

            if (r.Baskets.Count == 0 && r.IsInProcess)
                node.Items.Add(MakeMutedItem("(no basket info)"));
            foreach (var b in r.Baskets)
            {
                var bn = new TreeViewItem
                {
                    IsExpanded = true,
                    Header = $"篮 {b.DisplayId} (Id {b.BasketId}) · {b.NumberOfUsedCassettes}/{b.Capacity}",
                };
                if (b.Cassettes.Count == 0 && b.NumberOfUsedCassettes > 0)
                    bn.Items.Add(MakeMutedItem($"({b.NumberOfUsedCassettes} 个蜡块详情未返回)"));
                foreach (var c in b.Cassettes)
                {
                    string label = string.IsNullOrEmpty(c.HumanReadableId) ? $"Id={c.Id}" : c.HumanReadableId;
                    if (!string.IsNullOrEmpty(c.TissueType)) label += $" · {c.TissueType}";
                    bn.Items.Add(new TreeViewItem { Header = label });
                }
                node.Items.Add(bn);
            }
            return node;
        }

        private static object MakeStatusHeader(string text, bool warn)
        {
            var tb = new TextBlock { Text = text };
            if (warn) tb.Foreground = System.Windows.Media.Brushes.IndianRed;
            return tb;
        }

        private static object MakeMutedItem(string text) =>
            new TreeViewItem
            {
                Header = new TextBlock { Text = text, Foreground = System.Windows.Media.Brushes.Gray },
            };

        /// <summary>结束指定缸：SetRetortCassettesAsProcessed + 把缸内所有篮的蜡块标为已移除。</summary>
        private async Task EndExpiredRetortAsync(Retort r)
        {
            if (_ts == null) return;
            try
            {
                Log($"SetRetortCassettesAsProcessed(retortId={r.Id})");
                await _ts.SetRetortCassettesAsProcessedAsync(r.Id);
                foreach (var b in r.Baskets)
                {
                    var items = await _ts.GetTissueProcessingWorkListAsync(b.BasketId);
                    var ids = items.Select(w => w.BlockId).Where(x => x > 0).ToList();
                    if (ids.Count == 0) { Log($"  篮 {b.DisplayId} 为空，跳过"); continue; }
                    Log($"  SetCassettesRemovedFromBasket({ids.Count} 个, processorId={r.ProcessorId}, retortNumber={r.Number})");
                    await _ts.SetCassettesRemovedFromBasketAsync(ids,
                        App.WorkCellId, App.EmpUserId, r.ProcessorId, r.Number);
                }
                Log($"✓ Retort #{r.Number} 已结束");
            }
            catch (Exception ex)
            {
                Log("[错误] 结束超时缸失败: " + ex.Message);
            }
        }

        private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            await LoadDevicesAsync();
        }

        private async void CboBasket_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _currentBasket = (cboBasket.SelectedItem as ComboBoxItem)?.Tag as BasketInfo;
            await RefreshBasketItemsAsync();
        }

        /// <summary>从服务端拉当前篮的蜡块清单（GeTissueProcessingWorkList），渲染到预览。
        /// 用 _lastBasketSig 做指纹去抖：内容相同则不重建 ListBox 避免选中态被清空。</summary>
        private async Task RefreshBasketItemsAsync()
        {
            if (_ts == null || _currentBasket == null || _currentBasket.BasketId == 0)
            {
                _basketItems.Clear();
                lstObjects.Items.Clear();
                _lastBasketSig = "";
                UpdatePreviewLabel();
                return;
            }
            try
            {
                var fresh = await _ts.GetTissueProcessingWorkListAsync(_currentBasket.BasketId);
                string sig = _currentBasket.BasketId + "|" +
                    string.Join(",", fresh.Select(w => w.BlockId));
                if (sig != _lastBasketSig)
                {
                    _lastBasketSig = sig;
                    _basketItems = fresh;
                    lstObjects.Items.Clear();
                    foreach (var w in _basketItems) lstObjects.Items.Add(w);
                }
                else
                {
                    _basketItems = fresh;  // 内容一样但保持引用最新
                }
            }
            catch (Exception ex)
            {
                Log("[警告] 拉取篮内蜡块失败: " + ex.Message);
            }
            UpdatePreviewLabel();
        }

        private void UpdatePreviewLabel()
        {
            int inBasket = _basketItems.Count;
            int queued = _objects.Count;
            string s = $"包埋盒预览（篮内 {inBasket} 个";
            if (queued > 0) s += $"，待添加 {queued} 个";
            s += "）";
            lblPreview.Text = s;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            string? path = BatchPanelBase.PickFile("选择包埋盒编号文件");
            if (path == null) return;
            try
            {
                _objects = SlideListReader.Read(path);
                txtFile.Text = $"{path}（{_objects.Count} 个待添加）";
                Log($"读取到 {_objects.Count} 个包埋盒编号");
                UpdatePreviewLabel();
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnAddCassettes_Click(object sender, RoutedEventArgs e)
        {
            if (_ts == null) return;
            if (_currentBasket == null || _currentBasket.BasketId == 0)
            {
                MessageBox.Show("请先选择脱水框", "缺脱水框", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_objects.Count == 0)
            {
                MessageBox.Show("请先选择包埋盒编号文件", "无数据", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            btnAddCassettes.IsEnabled = false;
            try { await AddCassettesToBasketAsync(); }
            finally { btnAddCassettes.IsEnabled = true; }
        }

        private async void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_ts == null || _currentBasket == null) return;
            var selected = lstObjects.SelectedItems.OfType<WorkItem>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先选中要移除的包埋盒（按住 Ctrl/Shift 多选）", "未选中",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await RemoveCassettesFromCurrentBasketAsync(selected);
        }

        private async void BtnClearBasket_Click(object sender, RoutedEventArgs e)
        {
            if (_ts == null || _currentBasket == null) return;
            if (_basketItems.Count == 0) { Log("篮已空，无需清理"); return; }
            var ans = MessageBox.Show($"确认清空篮 {_currentBasket.DisplayId} 中全部 {_basketItems.Count} 个包埋盒？",
                "清空脱水框", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ans != MessageBoxResult.Yes) return;
            await RemoveCassettesFromCurrentBasketAsync(_basketItems.ToList());
        }

        private async Task RemoveCassettesFromCurrentBasketAsync(List<WorkItem> items)
        {
            if (_ts == null || _currentBasket == null) return;
            // RemoveCassettesFromBasket 需要 processorId / retortNumber。优先用篮当前所在的，
            // 若篮还没装载（ProcessorId=0），用当前选中的脱水机/缸做目标值（与 WPF 行为一致）
            int procId = _currentBasket.ProcessorId;
            int retNo = _currentBasket.RetortNumber;
            if (procId == 0 || retNo == 0)
            {
                if ((cboProcessor.SelectedItem as ComboBoxItem)?.Tag is TissueProcessor sp) procId = sp.Id;
                if ((cboRetort.SelectedItem as ComboBoxItem)?.Tag is Retort sr) retNo = sr.Number;
            }
            try
            {
                Log($"RemoveCassettesFromBasket({items.Count} 个, processorId={procId}, retortNumber={retNo})");
                await _ts.RemoveCassettesFromBasketAsync(items.Select(w => w.BlockId),
                    App.WorkCellId, App.EmpUserId, procId, retNo);
                Log($"✓ 已移除 {items.Count} 个包埋盒");
            }
            catch (Exception ex)
            {
                Log("[错误] 移除失败: " + ex.Message);
            }
            await RefreshBasketItemsAsync();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => _batchCts?.Cancel();

        /// <summary>把 _objects 里的所有蜡块加到当前篮（GetArtifact + AssignCassetteToBasket）。返回 (成功数, 失败数)。</summary>
        private async Task<(int Ok, int Fail)> AddCassettesToBasketAsync()
        {
            int basketId = _currentBasket!.BasketId;
            progress.Maximum = _objects.Count;
            progress.Value = 0;
            int ok = 0, fail = 0;
            _batchCts = new System.Threading.CancellationTokenSource();
            btnCancel.IsEnabled = true;
            var ct = _batchCts.Token;
            Log($"向 BasketId={basketId} 添加 {_objects.Count} 个包埋盒...");
            foreach (var sid in _objects)
            {
                if (ct.IsCancellationRequested) { Log("[中止] 用户取消"); break; }
                try
                {
                    var art = await _ts!.GetArtifactAsync(sid, App.WorkCellId, App.EmpUserId);
                    if (art == null || art.BlockId == 0)
                    {
                        Log($"  FAIL {sid}: 未找到");
                        fail++;
                    }
                    else if (art.IsCanceled)
                    {
                        Log($"  SKIP {sid}: 已取消");
                        fail++;
                    }
                    else
                    {
                        await _ts.AssignCassetteToBasketAsync(art.BlockId, basketId,
                            App.WorkCellId, App.EmpUserId);
                        Log($"  OK   {sid} (BlockId={art.BlockId})");
                        ok++;
                    }
                }
                catch (Exception ex)
                {
                    Log($"  FAIL {sid}: {ex.Message}");
                    fail++;
                }
                progress.Value++;
            }
            Log($"添加完成: 成功 {ok} 个, 失败 {fail} 个");
            btnCancel.IsEnabled = false;
            _batchCts = null;
            progress.Value = 0;
            await RefreshBasketItemsAsync();
            return (ok, fail);
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_ts == null) return;
            if (_currentBasket == null || _currentBasket.BasketId == 0)
            {
                MessageBox.Show("请先选择脱水框", "缺脱水框", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if ((cboProcessor.SelectedItem as ComboBoxItem)?.Tag is not TissueProcessor p
                || (cboRetort.SelectedItem as ComboBoxItem)?.Tag is not Retort r)
            {
                MessageBox.Show("请选择脱水机和脱水缸", "缺设备", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int? duration = ResolveDurationMinutes();
            if (duration == null) return;

            btnStart.IsEnabled = false;
            try
            {
                // 自动加蜡块到篮（如果文件已选且篮里数量不够）
                if (_objects.Count > 0)
                {
                    var bCheck = await _ts.GetBasketInfoByIdAsync(_currentBasket.BasketId);
                    if (bCheck != null && bCheck.NumberOfUsedCassettes < _objects.Count)
                    {
                        Log($"篮当前已用 {bCheck.NumberOfUsedCassettes}/{bCheck.Capacity}，文件 {_objects.Count} 个——自动添加");
                        var (addOk, addFail) = await AddCassettesToBasketAsync();
                        if (addOk == 0 && addFail > 0)
                        {
                            Log("[错误] 没有任何蜡块成功添加到篮，已中止启动");
                            return;
                        }
                    }
                }

                // 调用前：查一遍篮和缸当前状态
                var bBefore = await _ts.GetBasketInfoByIdAsync(_currentBasket.BasketId);
                Log($"[调用前] BasketId={_currentBasket.BasketId} ProcessorId={bBefore?.ProcessorId} " +
                    $"RetortNumber={bBefore?.RetortNumber} 已用蜡块={bBefore?.NumberOfUsedCassettes} 容量={bBefore?.Capacity}");
                var rBefore = await _ts.GetRetortInfoAsync(p.Id, r.Number);
                Log($"[调用前] RetortId={r.Id} IsInProcess={rBefore?.IsInProcess} Duration={rBefore?.Duration}");

                // 前置检查
                if (rBefore != null && rBefore.IsInProcess)
                {
                    var ans = MessageBox.Show(
                        $"脱水缸 #{r.Number} 当前正在运行（Duration={rBefore.Duration}），无法启动新批次。\n\n" +
                        "是否先强制结束当前批次？\n" +
                        "（会调用 SetRetortCassettesAsProcessed + RemoveBasketFromRetort）",
                        "缸正忙", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (ans != MessageBoxResult.Yes) return;
                    Log($"先清理 Retort#{r.Number}：SetRetortCassettesAsProcessed");
                    try { await _ts.SetRetortCassettesAsProcessedAsync(r.Id); }
                    catch (Exception ex) { Log("  [警告] SetRetortCassettesAsProcessed: " + ex.Message); }
                    foreach (var bb in (rBefore?.Baskets ?? new List<BasketInfo>()))
                    {
                        try
                        {
                            var items = await _ts.GetTissueProcessingWorkListAsync(bb.BasketId);
                            var ids = items.Select(w => w.BlockId).Where(x => x > 0).ToList();
                            if (ids.Count == 0) continue;
                            Log($"  SetCassettesRemovedFromBasket(BasketId={bb.BasketId}, {ids.Count} 个)");
                            await _ts.SetCassettesRemovedFromBasketAsync(ids,
                                App.WorkCellId, App.EmpUserId, p.Id, r.Number);
                        }
                        catch (Exception ex) { Log("  [警告] 清理篮失败: " + ex.Message); }
                    }
                    var rRecheck = await _ts.GetRetortInfoAsync(p.Id, r.Number);
                    if (rRecheck == null || rRecheck.IsInProcess)
                    {
                        Log("[错误] 清理后 Retort 仍在运行，无法启动新批次");
                        return;
                    }
                    Log("✓ 缸已清理，IsInProcess=false");
                }
                if (bBefore != null && bBefore.ProcessorId != 0 && bBefore.ProcessorId != p.Id)
                {
                    MessageBox.Show($"该篮已在另一台脱水机（ProcessorId={bBefore.ProcessorId}），无法装载到本机", "篮被占用");
                    return;
                }

                Log($"AssignBasketToRetort(basketId={_currentBasket.BasketId}, processorId={p.Id}, " +
                    $"retortNumber={r.Number}, retortId={r.Id}, workCellId={App.WorkCellId}, userId={App.EmpUserId})");
                await _ts.AssignBasketToRetortAsync(_currentBasket.BasketId, p.Id, r.Number,
                    r.Id, App.WorkCellId, App.EmpUserId);

                // AssignBasketToRetort 后：必须验证篮真的进缸了，否则中止
                var bAfter1 = await _ts.GetBasketInfoByIdAsync(_currentBasket.BasketId);
                Log($"[Assign 后] Basket: ProcessorId={bAfter1?.ProcessorId} RetortNumber={bAfter1?.RetortNumber}");
                if (bAfter1 == null || bAfter1.ProcessorId != p.Id || bAfter1.RetortNumber != r.Number)
                {
                    Log("[错误] AssignBasketToRetort 服务端未生效，已中止——不会启动空缸");
                    return;
                }

                Log($"UpdateRetortProcessingState(retortId={r.Id}, durationInMinutes={duration}, startNow=true)");
                await _ts.UpdateRetortProcessingStateAsync(r.Id, duration.Value, startNow: true);

                var rAfter = await _ts.GetRetortInfoAsync(p.Id, r.Number);
                Log($"[Update 后] Retort: IsInProcess={rAfter?.IsInProcess} Duration={rAfter?.Duration} " +
                    $"StartTime={rAfter?.StartTime:HH:mm:ss}");
                if (rAfter == null || !rAfter.IsInProcess)
                    Log("[严重] UpdateRetortProcessingState 服务端未生效——IsInProcess 仍为 false");
                else
                    Log("✓ 脱水已启动（已验证服务端状态）");

                // 启动成功后立即刷新状态树
                _expiredPrompted.Clear();
                await RefreshProcessorStatusAsync(p, promptExpired: false);
            }
            catch (Exception ex)
            {
                Log("[错误] 启动失败: " + ex.Message);
            }
            finally
            {
                btnStart.IsEnabled = true;
            }
        }

        private async void BtnEnd_Click(object sender, RoutedEventArgs e)
        {
            if (_ts == null) return;
            if (_currentBasket == null || _currentBasket.BasketId == 0)
            {
                MessageBox.Show("请先选择脱水框", "缺脱水框", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if ((cboProcessor.SelectedItem as ComboBoxItem)?.Tag is not TissueProcessor p
                || (cboRetort.SelectedItem as ComboBoxItem)?.Tag is not Retort r)
            {
                MessageBox.Show("请选择脱水机和脱水缸", "缺设备", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool stillRunning = false;
            try
            {
                var fresh = await _ts.GetRetortInfoAsync(p.Id, r.Number);
                stillRunning = fresh?.IsInProcess ?? false;
            }
            catch (Exception ex)
            {
                Log("[警告] 查询缸状态失败，按强制结束处理: " + ex.Message);
                stillRunning = true;
            }

            if (stillRunning)
            {
                var ans = MessageBox.Show(
                    $"脱水缸 #{r.Number} 仍在运行。是否强制结束？\n（标蜡块已处理 + 卸下篮中蜡块）",
                    "强制结束", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ans != MessageBoxResult.Yes) return;
            }

            // 拉一遍当前篮里的蜡块 ID 列表，传给 SetCassettesRemovedFromBasket
            await RefreshBasketItemsAsync();
            var ids = _basketItems.Select(w => w.BlockId).Where(id => id > 0).ToList();

            btnEnd.IsEnabled = false;
            try
            {
                Log($"SetRetortCassettesAsProcessed(retortId={r.Id})");
                await _ts.SetRetortCassettesAsProcessedAsync(r.Id);

                if (ids.Count > 0)
                {
                    Log($"SetCassettesRemovedFromBasket({ids.Count} 个蜡块, " +
                        $"processorId={p.Id}, retortNumber={r.Number})");
                    await _ts.SetCassettesRemovedFromBasketAsync(ids,
                        App.WorkCellId, App.EmpUserId, p.Id, r.Number);
                }
                else
                {
                    Log("[警告] 篮内无蜡块，跳过 SetCassettesRemovedFromBasket");
                }
                Log("✓ 脱水已结束");

                _expiredPrompted.Clear();
                await RefreshProcessorStatusAsync(p, promptExpired: false);
                await RefreshBasketItemsAsync();
            }
            catch (Exception ex)
            {
                Log("[错误] 结束失败: " + ex.Message);
            }
            finally
            {
                btnEnd.IsEnabled = true;
            }
        }

        /// <summary>把"持续时间"或"结束时间"两种模式都换算成分钟数。失败弹框并返回 null。</summary>
        private int? ResolveDurationMinutes()
        {
            if (rbDuration.IsChecked == true)
            {
                int.TryParse(txtHours.Text.Trim(), out int h);
                int.TryParse(txtMinutes.Text.Trim(), out int m);
                int total = h * 60 + m;
                if (total <= 0)
                {
                    MessageBox.Show("请输入有效的时长（小时+分钟，至少 1 分钟）", "无效输入",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }
                return total;
            }
            if (dpEndDate.SelectedDate == null)
            {
                MessageBox.Show("请选择结束日期", "无日期", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            if (!TimeSpan.TryParseExact(txtEndTime.Text.Trim(), @"h\:m", CultureInfo.InvariantCulture, out var t)
                && !TimeSpan.TryParseExact(txtEndTime.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out t))
            {
                MessageBox.Show("结束时间格式应为 HH:mm，例如 08:30", "无效时间",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            DateTime end = dpEndDate.SelectedDate.Value.Date + t;
            int diff = (int)Math.Round((end - DateTime.Now).TotalMinutes);
            if (diff <= 0)
            {
                MessageBox.Show("结束时间必须晚于当前时间", "无效时间",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return diff;
        }

        private void Log(string msg) => BatchPanelBase.Log(logBox, logScroll, nameof(DehydrationPanel), msg);
    }
}
