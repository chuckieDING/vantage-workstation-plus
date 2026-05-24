using System.Windows;
using VantageWorkstationPlus.Services;

namespace VantageWorkstationPlus.ConfigTool
{
    public partial class EncryptDialog : Window
    {
        public EncryptDialog() => InitializeComponent();

        private void TxtPwd_PasswordChanged(object sender, RoutedEventArgs e)
        {
            txtOut.Text = string.IsNullOrEmpty(txtPwd.Password)
                ? "" : SecretProtector.Encrypt(txtPwd.Password);
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtOut.Text)) return;
            Clipboard.SetText(txtOut.Text);
            MessageBox.Show("已复制到剪贴板", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
