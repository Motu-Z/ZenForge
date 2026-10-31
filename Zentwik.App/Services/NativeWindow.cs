using System;
using System.Runtime.InteropServices;

namespace Zentwik.App.Services;

/// <summary>
/// 窗口相关的少量 Win32 调用。
///
/// 【为什么需要它】
/// <c>XamlRoot.RasterizationScale</c> 在根节点 Loaded 时还是 1.0 —— 此时窗口尚未激活，
/// 合成器还没把真实缩放比写进去。用它去算客户区像素，在 150% 缩放的机器上会得到
/// 一个 1120px 的窗口，而内容仍按 1.5 倍渲染：侧栏、卡片全部超出窗口右边界，
/// 看起来像「布局崩了」，实际是尺寸单位错了。
/// <c>GetDpiForWindow</c> 不依赖 XAML 时间线，窗口一创建就是有效值。
/// </summary>
internal static partial class NativeWindow
{
    /// <summary>返回窗口所在显示器的 DPI（96 = 100%）。</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hwnd);
}
