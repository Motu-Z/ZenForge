using System.Windows;
using System.Windows.Media;

namespace ZenForge.Helpers
{
    /// <summary>
    /// 按当前 DPI 选择文本格式化模式（Helper 层：纯静态工具，不持有状态）。
    ///
    /// 【为什么不能全局写死 TextFormattingMode=Display】
    /// Display 用的是 GDI 兼容度量：每个字形的宽度被压成整数像素、对齐到像素网格。
    /// 在 100% 缩放下这是好事 —— 小字的边缘清脆，不会因为亚像素定位发灰。
    /// 但这条优势有两个前提，高 DPI 下两个都不成立：
    ///
    ///   1. 「像素网格」在 150% / 200% 下已经足够细，对齐收益消失，
    ///      剩下的只有字形被 hinting 拉变形的代价；
    ///   2. 官方明确：Display 模式的文本度量**不随缩放线性变化**（Ideal 模式
    ///      下 2.0 倍缩放的包围盒恰好是 1.0 倍的两倍，Display 不是），
    ///      一旦元素带 RenderTransform，Display 的文字会发糊、出伪影。
    ///
    /// 本项目两个场景正好都踩到：多显示器 150% 缩放常见，而进程列表的行
    /// 从 v1.0.4 起重排时带位移过渡（RenderTransform）。所以模式必须按 DPI 选，
    /// 不能写死在 XAML 里。
    ///
    /// 【为什么用属性继承而不是遍历改每个 TextBlock】
    /// TextOptions.TextFormattingMode 的元数据带 Inherits，设在窗口根元素上
    /// 会自动传给整棵可视树（含后加入的行、弹层），不必在每次列表刷新时重设。
    /// </summary>
    public static class UiTextOptions
    {
        /// <summary>
        /// 高 DPI 门槛：150% 缩放起走 Ideal。
        /// 125% 仍算「网格不够细」的区间，Display 的像素对齐还有正收益，不动它。
        /// </summary>
        private const double HighDpiThreshold = 1.25;

        /// <summary>
        /// 窗口挂载：首次按当前 DPI 定模式，之后跟随 DPI 变化（拖到另一块屏）。
        /// </summary>
        public static void Attach(Window window)
        {
            // 构造阶段窗口还没有 HWND，此刻 GetDpi 只能拿到 96dpi 的默认值，
            // 据此选模式会选错；必须等 Loaded（可视树与 HWND 都已就位）再定。
            if (window.IsLoaded)
                Apply(window, VisualTreeHelper.GetDpi(window).DpiScaleX);
            else
                window.Loaded += (_, _) => Apply(window, VisualTreeHelper.GetDpi(window).DpiScaleX);

            // PerMonitorV2 下 DPI 会随窗口所在显示器变化，不跟随就会出现
            // 「拖到 200% 的屏上，文字模式还停在 100% 那一档」。
            window.DpiChanged += (_, e) => Apply(window, e.NewDpi.DpiScaleX);
        }

        /// <summary>按给定缩放倍数设定格式化模式。</summary>
        public static void Apply(FrameworkElement root, double dpiScaleX)
        {
            TextOptions.SetTextFormattingMode(root,
                dpiScaleX > HighDpiThreshold ? TextFormattingMode.Ideal : TextFormattingMode.Display);
        }
    }
}
