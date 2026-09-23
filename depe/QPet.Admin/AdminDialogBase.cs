using System.Windows;
using System.Windows.Controls;
using QPet.Admin.Services;
using QPet.Ui;

namespace QPet.Admin;

/// <summary>
/// 管理端对话框基类:统一管理 API 调用(401 判定 + 错误提示)与结果文本显示。
/// 子类 XAML 根元素仍是 &lt;Window x:Class&gt;,code-behind 继承本类并实现 MsgBox 即可。
/// </summary>
public abstract class AdminDialogBase : Window
{
    protected AdminDialogBase(AdminApiClient api) => Api = api;

    /// <summary>管理 API 客户端(基类 PostAsync 与子类直接调用共用)。</summary>
    protected AdminApiClient Api { get; }

    /// <summary>错误/成功提示文本控件(子类 XAML 里定义的 ErrText 等)。</summary>
    protected abstract TextBlock MsgBox { get; }

    /// <summary>错误提示(红)。</summary>
    protected void ShowErr(string msg)
    {
        MsgBox.Foreground = UiPalette.Error;
        MsgBox.Text = msg;
        MsgBox.Visibility = Visibility.Visible;
    }

    /// <summary>成功提示(绿,复用错误文本位)。</summary>
    protected void ShowSuccess(string msg)
    {
        MsgBox.Foreground = UiPalette.Success;
        MsgBox.Text = msg;
        MsgBox.Visibility = Visibility.Visible;
    }

    /// <summary>POST 管理端点:401 统一提示登录过期,失败显示错误,成功返回 true。</summary>
    protected async Task<bool> PostAsync(string endpoint, object body)
    {
        try
        {
            using var r = await Api.PostAsync(endpoint, body);
            if (r.Unauthorized)
            {
                ShowErr("登录已过期,请重新登录管理端");
                return false;
            }
            if (!r.IsSuccess)
            {
                ShowErr($"操作失败 ({r.Status}) {r.Error}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            ShowErr("请求失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>生成用户勾选框("昵称 (账号)" 文案 + WarmCheckBox 样式),Tag 挂业务 key。</summary>
    protected CheckBox MakeUserCheckBox(string account, string nickname, string tag)
    {
        return new CheckBox
        {
            Content = $"  {(nickname.Length > 0 ? $"{nickname} ({account})" : account)}",
            Tag = tag,
            Margin = new Thickness(2, 5, 0, 5),
            Style = (Style)FindResource("WarmCheckBox"),
        };
    }
}
