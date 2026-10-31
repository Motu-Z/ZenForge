using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Zentwik.App.Controls;

/// <summary>
/// 全局下拉框。存在的唯一理由是「宽度对齐」这一件模板做不到的事。
///
/// 【为什么不直接给 ComboBox 套一个重模板就完事】
/// 下拉弹层的宽度需要等于控件宽度，否则短文案的下拉框展开后是一个只有 60px 的窄条，
/// 长文案（如「Above Normal」）被截断。系统模板用 ElementName 绑定解决这件事，
/// 但资源字典里的 ControlTemplate 没有 x:Class 命名域，{x:Bind} 不可用；
/// 退回 {Binding ElementName=...} 就是运行期反射绑定，违反「零反射」。
///
/// 因此模板只负责外观，宽度同步放在这里：OnApplyTemplate 拿到 Popup 之后
/// 把 Popup.Child 的 MinWidth 对齐控件实际宽度，并在 SizeChanged 时更新。
/// </summary>
public sealed partial class AppComboBox : ComboBox
{
    private Popup? _popup;

    public AppComboBox()
    {
        // 不设这一行，样式解析会继续用 ComboBox 的默认样式键（即系统白底模板），
        // 本类自己的隐式样式永远不会命中。
        DefaultStyleKey = typeof(AppComboBox);
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _popup = GetTemplateChild("Popup") as Popup;
        SyncPopupWidth();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncPopupWidth();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => SyncPopupWidth();

    private void SyncPopupWidth()
    {
        if (_popup?.Child is not FrameworkElement child)
            return;

        double width = ActualWidth;
        if (width <= 0)
            return;

        // 用 MinWidth 而不是 Width：展开后弹层仍可因超长文案被撑宽，
        // 但绝不会窄于下拉框本身（对齐感来自「不窄于」，不是「必须相等」）。
        child.MinWidth = width;
    }
}
