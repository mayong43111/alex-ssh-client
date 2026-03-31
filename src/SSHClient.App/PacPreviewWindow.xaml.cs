using System.Windows;

namespace SSHClient.App;

public partial class PacPreviewWindow : Window
{
    public PacPreviewWindow(string pacScript)
    {
        InitializeComponent();
        PacTextBox.Text = pacScript ?? string.Empty;
        PacTextBox.CaretIndex = 0;
        PacTextBox.ScrollToHome();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PacTextBox.Text ?? string.Empty);
            MessageBox.Show(this, "PAC 已复制到剪贴板。", "PAC 预览", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制失败：{ex.Message}", "PAC 预览", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
