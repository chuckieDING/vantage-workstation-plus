using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VantageWorkstationPlus.Panels;

namespace VantageWorkstationPlus
{
    public partial class MainWindow : Window
    {
        private readonly SignOffPanel _signOff = new();
        private readonly DehydrationPanel _dehydration = new();
        private readonly ArchivePanel _archive = new();
        private readonly TransferPanel _transfer = new();
        private bool _confirmedExit;

        public RelayCommand SwitchTab1 { get; }
        public RelayCommand SwitchTab2 { get; }
        public RelayCommand SwitchTab3 { get; }
        public RelayCommand SwitchTab4 { get; }
        public RelayCommand OpenSettings { get; }
        public RelayCommand ShowHelp { get; }

        public MainWindow()
        {
            SwitchTab1 = new RelayCommand(() => SelectTabByIndex(0));
            SwitchTab2 = new RelayCommand(() => SelectTabByIndex(1));
            SwitchTab3 = new RelayCommand(() => SelectTabByIndex(2));
            SwitchTab4 = new RelayCommand(() => SelectTabByIndex(3));
            OpenSettings = new RelayCommand(OpenSettingsDialog);
            ShowHelp = new RelayCommand(ShowHelpDialog);

            InitializeComponent();
            Loaded += (_, _) =>
            {
                lblWelcome.Text = App.Session?.Username
                    ?? App.SoapSession?.LoggedInUser?.UserName ?? "";
                lblTitle.Text = "批量工作站";

                // 应用 appsettings.EnabledTabs：未列入的 Tab 直接折叠掉
                if (App.EnabledTabs != null)
                {
                    foreach (var item in lstMenu.Items.OfType<ListBoxItem>())
                    {
                        string tag = (item.Tag as string)?.ToLowerInvariant() ?? "";
                        if (!App.EnabledTabs.Contains(tag))
                            item.Visibility = Visibility.Collapsed;
                    }
                }
                int firstVisible = -1;
                for (int i = 0; i < lstMenu.Items.Count; i++)
                {
                    if (lstMenu.Items[i] is ListBoxItem li && li.Visibility == Visibility.Visible)
                    { firstVisible = i; break; }
                }
                if (firstVisible >= 0) lstMenu.SelectedIndex = firstVisible;
            };
        }

        /// <summary>按"可见 Tab 列表里的索引"选中（Ctrl+1..4 用），跳过隐藏的。</summary>
        private void SelectTabByIndex(int visibleIndex)
        {
            int seen = -1;
            for (int i = 0; i < lstMenu.Items.Count; i++)
            {
                if (lstMenu.Items[i] is ListBoxItem li && li.Visibility == Visibility.Visible)
                {
                    seen++;
                    if (seen == visibleIndex) { lstMenu.SelectedIndex = i; return; }
                }
            }
        }

        private void OpenSettingsDialog()
        {
            var dlg = new Dialogs.SettingsDialog { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                MessageBox.Show("设置已保存。重启程序生效。", "保存成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ShowHelpDialog()
        {
            MessageBox.Show(
                "Histology Workstation Plus — 批量工作站\n\n" +
                "快捷键：\n" +
                "  Ctrl+1..4    切换到对应 Tab\n" +
                "  Ctrl+,       打开设置\n" +
                "  F1           显示本帮助\n\n" +
                "使用步骤：\n" +
                "  1. 登录服务器 + 用户名 + 密码\n" +
                "  2. 在左侧选功能 Tab\n" +
                "  3. 选好参数后点「下载模板」生成 xlsx 模板\n" +
                "  4. 编辑模板填好要操作的 ID\n" +
                "  5. 点「选择...」上传文件，再点开始批量\n\n" +
                "配置：右上「设置」按钮可改 BaseUrl / WorkCellId 等。\n" +
                "日志：./logs/{date}.log 自动落盘。",
                "帮助", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void BtnSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsDialog();
        private void BtnHelp_Click(object sender, RoutedEventArgs e) => ShowHelpDialog();

        private void Title_DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && WindowState != WindowState.Maximized)
                DragMove();
        }

        private void LstMenu_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstMenu.SelectedItem is not ListBoxItem item) return;
            string title = ((TextBlock)item.Content).Text;
            lblTitle.Text = title;
            content.Content = item.Tag switch
            {
                "signoff" => _signOff,
                "dehydration" => _dehydration,
                "archive" => _archive,
                "transfer" => _transfer,
                _ => null,
            };
        }

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            var ans = MessageBox.Show("确认切换账号？当前会话将退出。", "切换账号",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) return;
            App.Session?.Dispose();
            App.SoapSession?.Dispose();
            App.Session = null;
            App.SoapSession = null;
            var login = new Dialogs.LoginDialog();
            Application.Current.MainWindow = login;
            login.Show();
            _confirmedExit = true; // 切换不算"退出确认"，避免 Closing 再弹一次
            Close();
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_confirmedExit) return;
            var ans = MessageBox.Show("确认退出程序？", "退出确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _confirmedExit = true;
            Application.Current.Shutdown();
        }
    }

    /// <summary>WPF KeyBinding 用的最简 RelayCommand。</summary>
    public class RelayCommand : ICommand
    {
        private readonly System.Action _exec;
        public RelayCommand(System.Action exec) => _exec = exec;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _exec();
        public event System.EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
