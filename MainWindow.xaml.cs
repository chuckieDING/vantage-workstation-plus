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

        public MainWindow()
        {
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

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

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
}
