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
            txtUrl.Text = Properties.Settings.Default.BaseUrl ?? "http://192.168.127.128";
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
                        "点击「是」立即向服务器注册：\n" +
                        " 1) SecurityHandshake（写入加密密钥）\n" +
                        " 2) SaveStation（创建 TissueProcessing 工作站实例）\n\n" +
                        "⚠ 如果本机已安装并在用 WPF 客户端，注册会覆盖原凭据。",
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
                    // 拉一遍 WorkCells 列表给提示
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
                        "appsettings.json 里的 WorkCellId 仍是默认值或未配置。\n" +
                        "请先在虚机上跑原版 clientsetup.exe 注册工作站，\n" +
                        "然后把分配到的 WorkCellID 填进 appsettings.json 的 WorkCellId 字段。" + hint);
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

                Properties.Settings.Default.BaseUrl = url;
                Properties.Settings.Default.Save();

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
