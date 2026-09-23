using System.Windows;

namespace QPet.Ui.Dialogs;

/// <summary>
/// 确认/提示弹窗(替代系统 MessageBox,双端共用):
/// 按钮集与文案由调用点决定;确定返回 true,取消/关闭返回 null。
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, bool showCancel = true,
        string okText = "确定", string cancelText = "取消")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        if (showCancel)
        {
            CancelButton.Content = cancelText;
        }
        else
        {
            CancelButton.Visibility = Visibility.Collapsed; // 纯提示:只留确定
            OkButton.Margin = new Thickness(0, 0, 0, 0);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
