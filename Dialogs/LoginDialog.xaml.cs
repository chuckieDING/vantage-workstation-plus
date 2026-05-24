using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using VantageWorkstationPlus.Services;

namespace VantageWorkstationPlus.Dialogs
{
    public partial class LoginDialog : Window
    {
        public LoginDialog()
        {
            InitializeComponent();
            txtUrl.Text = App.BaseUrl;
            lblServerInfo.Text = $"服务器: {App.BaseUrl}  ·  工作站 ID: " +
                (App.WorkCellId > 0 ? App.WorkCellId.ToString() : "未配置");
            txtPassword.Focus();
        }

        private void Title_DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string url = txtUrl.Text.Trim();
            string user = txtUsername.Text.Trim();
            string pwd = txtPassword.Password;

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pwd))
            {
                lblError.Text = "请填写服务器、用户名和密码";
                return;
            }

            btnLogin.IsEnabled = false;
            lblError.Text = "登录中...";
            try
            {
                // SOAP 鉴权底座（环境变量）；不存在则提示注册
                TouchScreenSession ts;
                try
                {
                    var auth = SoapAuth.FromEnvironment();
                    auth.ClientVersion = App.ClientVersion;
                    ts = new TouchScreenSession(url, auth, App.AcceptAnyServerCert);
                }
                catch (WorkstationNotRegisteredException)
                {
                    var ans = MessageBox.Show(
                        $"本机（MAC={SoapAuth.GetMacAddress()}）尚未注册为工作站。\n\n" +
                        "检测到的所有 Up 网卡：\n" + SoapAuth.DiagnoseNics() + "\n" +
                        "如果上面某张卡显示「有 NPLA_ENCRYPTION_KEY env var」但跟我们选的不一致，\n" +
                        "说明 WPF 注册到了那张卡。请把 WPF 注册的那张卡禁用，或调整网卡启用顺序。\n\n" +
                        "点击「是」立即向当前 MAC 重新注册：\n" +
                        " 1) SecurityHandshake（写入加密密钥）\n" +
                        " 2) SaveStation（创建 TissueProcessing 工作站实例）\n\n" +
                        "⚠ 如果本机已在用 WPF 客户端，注册会覆盖原凭据。",
                        "注册工作站", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ans != MessageBoxResult.Yes)
                    {
                        lblError.Text = "登录已取消（未注册）";
                        return;
                    }
                    lblError.Text = "握手中…";
                    ts = await TouchScreenSession.RegisterWorkstationAsync(url, App.ClientVersion);
                    lblError.Text = "握手成功，正在创建工作站…";
                }

                // WorkCellId：appsettings 优先，env var 兜底；都没有则报错
                if (App.WorkCellId <= 0)
                {
                    int? envWc = SoapAuth.GetSavedWorkCellId();
                    if (envWc != null && envWc.Value > 0) App.WorkCellId = envWc.Value;
                }
                if (App.WorkCellId <= 0)
                {
                    // 仿原版 WPF：读注册表 HKLM\SOFTWARE\Ventana\VANTAGE\MachineID
                    // → SOAP GetWorkstationConfigInfo(machid) → 拿 WorkCellID，无需用户手填
                    string? machId = SoapAuth.GetMachineIdFromRegistry();
                    if (!string.IsNullOrEmpty(machId))
                    {
                        try
                        {
                            lblError.Text = $"从注册表读 MachineID={machId}，向服务器查 WorkCellID…";
                            var cfg = await ts.GetWorkstationConfigInfoAsync(machId);
                            if (cfg != null && cfg.WorkCellID > 0)
                            {
                                App.WorkCellId = cfg.WorkCellID;
                                SoapAuth.SaveWorkCellId(cfg.WorkCellID);
                                lblError.Text = $"自动取到 WorkCellID={cfg.WorkCellID} ({cfg.WorkcellName})";
                            }
                        }
                        catch (Exception ex)
                        {
                            // 不致命，继续走下面的报错提示
                            AppLog.Warn("GetWorkstationConfigInfo failed: " + ex.Message);
                        }
                    }
                }
                if (App.WorkCellId <= 0)
                {
                    string hint = "";
                    try
                    {
                        var cells = await ts.GetWorkCellsAsync();
                        var tps = cells.Where(c => c.WorkcellTypeID == TouchScreenSession.WorkCellTypeTissueProcessing).ToList();
                        if (tps.Count > 0)
                            hint = "\n\n服务器上现有的 TissueProcessing 工作站：\n" +
                                string.Join("\n", tps.Select(c => $"  WorkCellID={c.WorkCellID}  {c.WorkcellName} (MAC={c.MACAddress})"));
                    }
                    catch { }
                    throw new InvalidOperationException(
                        "未能自动取到 WorkCellID：注册表无 MachineID（clientsetup.exe 未跑过）" +
                        "且 appsettings.json / 环境变量都没配。\n\n" +
                        "请先在虚机跑原版 clientsetup.exe 注册工作站，或手动把 WorkCellID 填到 appsettings.json。" + hint);
                }

                // SOAP 预检：调一个不需要用户验证的方法，确认 AuthHeader 通；如果挂了说明工作站密钥有问题
                try
                {
                    lblError.Text = "AuthHeader 预检中…";
                    var procs = await ts.GetTissueProcessorsAsync();
                    if (procs.Count == 0)
                        throw new InvalidOperationException("AuthHeader 通但 GetTissueProcessors 返空，工作站可能没绑定脱水机");
                }
                catch (Exception preEx)
                {
                    throw new InvalidOperationException(
                        "SOAP 预检失败（AuthHeader 或 SOAP 连接异常）：" + preEx.Message +
                        "\n建议：清掉环境变量 {MAC}_NPLA_ENCRYPTION_KEY / _PASSWORD / _WORKCELL_ID / _MACH_ID，重跑 clientsetup.exe 重新注册。", preEx);
                }

                // SOAP 登录（脱水用）；失败把响应体 dump 出来辅助排查
                try
                {
                    await ts.LoginAsync(user, pwd, App.WorkCellType, App.WorkCellId);
                }
                catch (Exception loginEx)
                {
                    try
                    {
                        string dumpPath = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(), "vantage_login_fail.xml");
                        System.IO.File.WriteAllText(dumpPath,
                            $"<!-- WorkCellId={App.WorkCellId} WorkCellType={App.WorkCellType} -->\n" +
                            ts.LastResponseBody);
                        throw new InvalidOperationException(
                            loginEx.Message + $"\n（最后响应体 dump → {dumpPath}）", loginEx);
                    }
                    catch (InvalidOperationException) { throw; }
                    catch { throw loginEx; }
                }

                // Web 登录（出片 / 归档 / 流转用）
                var webSess = new AppSession(url, App.AcceptAnyServerCert);
                await webSess.LoginAsync(user, pwd);

                App.Session = webSess;
                App.SoapSession = ts;
                if (ts.LoggedInUser != null) App.EmpUserId = ts.LoggedInUser.Id;

                App.BaseUrl = url;  // 内存里记一下，本次会话用；持久化靠 appsettings.json

                var main = new MainWindow();
                Application.Current.MainWindow = main;
                main.Show();
                Close();
            }
            catch (Exception ex)
            {
                lblError.Text = "登录失败: " + ex.Message;
            }
            finally
            {
                btnLogin.IsEnabled = true;
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            var ans = MessageBox.Show("确认退出程序？", "退出确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes) Application.Current.Shutdown();
        }
    }
}
