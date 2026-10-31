using Microsoft.UI.Xaml.Controls;

namespace Zentwik.App.Views;

/// <summary>
/// 尚未实现的页面占位。
///
/// 存在的意义只有一个：导航栏的七个入口都是可点的，点进去不会掉回调校页。
/// 页面本身没有任何 ViewModel —— 它不消费业务数据，也就不该被塞一个空壳 VM。
/// </summary>
public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage(string title)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = "此页尚未实现";
    }
}
