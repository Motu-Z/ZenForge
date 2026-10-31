namespace Zentwik.App.ViewModels;

/// <summary>
/// 界面提示等级。
///
/// 刻意不用 <c>InfoBarSeverity</c>：那是 WinUI 的类型，一旦进了 ViewModel，
/// ViewModel 就再也不能在非 UI 宿主（单元测试、未来的 CLI）里构造。
/// 页面用 x:Bind 函数把它翻译成 InfoBarSeverity。
/// </summary>
public enum UiSeverity
{
    Info,
    Success,
    Warning,
    Error
}
