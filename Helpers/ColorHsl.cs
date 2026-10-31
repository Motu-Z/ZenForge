using System;
using System.Globalization;
using System.Windows.Media;

namespace Zentwik.Helpers
{
    /// <summary>
    /// HSL / RGB 互转。
    ///
    /// 为什么需要它：设置页的强调色选择器是「色相滑杆 + 色值输入框」两件套，
    /// 滑杆只控制色相（H），饱和度与亮度固定为 S=100% / L=70%。
    /// 而 WPF 的 <see cref="Color"/> 只有 RGB，没有 H 分量，
    /// 所以「当前颜色 → 滑杆位置」这一步必须自己算回来。
    ///
    /// 为什么不直接按 <see cref="Color.GetHue"/>：那是 WPF 自带的、只返回色相的方法，
    /// 但它对灰色的返回值是 0（无意义），而且拿不到 S / L。
    /// 自己实现一份完整的三值转换，逻辑集中在一个文件里，比散落各处的近似算法好维护。
    ///
    /// 公式来源：CSS Color Module Level 3 的 HSL 定义，与浏览器
    /// <c>hsl()</c> 的行为一致 —— 这样色值和用户在其他工具里看到的数字能对上。
    /// </summary>
    public static class ColorHsl
    {
        /// <summary>滑杆使用的固定饱和度（百分比）。</summary>
        public const double DefaultSaturation = 100.0;

        /// <summary>滑杆使用的固定亮度（百分比）。70% 时各色相都足够明亮，压在深色底上也清楚。</summary>
        public const double DefaultLightness = 70.0;

        /// <summary>
        /// HSL → RGB。<paramref name="h"/> 单位是度（0–360，允许负数与超过 360，内部取模），
        /// <paramref name="s"/> 与 <paramref name="l"/> 是百分比（0–100）。
        /// </summary>
        public static Color FromHsl(double h, double s, double l)
        {
            h = ((h % 360.0) + 360.0) % 360.0;
            s = Clamp(s, 0, 100) / 100.0;
            l = Clamp(l, 0, 100) / 100.0;

            // 灰阶：饱和度 0 时 RGB 三值相同，走通用公式会得到 0/0，必须单独处理
            if (s <= double.Epsilon)
            {
                byte gray = ToByte(l);
                return Color.FromRgb(gray, gray, gray);
            }

            double a = s * Math.Min(l, 1.0 - l);

            static double Channel(double n, double hue, double a, double l)
            {
                double k = (n + hue / 30.0) % 12.0;
                double value = l - a * Math.Max(Math.Min(k - 3.0, 9.0 - k), -1.0);
                return value;
            }

            return Color.FromRgb(
                ToByte(Channel(0, h, a, l)),
                ToByte(Channel(8, h, a, l)),
                ToByte(Channel(4, h, a, l)));
        }

        /// <summary>
        /// RGB → HSL。返回 (H 度, S 百分比, L 百分比)。
        /// 灰色（R=G=B）的 H 返回 0，调用方自己决定要不要沿用滑杆上一次的色相。
        /// </summary>
        public static (double H, double S, double L) ToHsl(Color c)
        {
            double r = c.R / 255.0;
            double g = c.G / 255.0;
            double b = c.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2.0;
            double delta = max - min;

            if (delta <= 1e-9)
                return (0.0, 0.0, l * 100.0);   // 无彩色

            double s = delta / (1.0 - Math.Abs(2.0 * l - 1.0));
            double h;

            if (Math.Abs(max - r) < 1e-9)
                h = 60.0 * (((g - b) / delta) % 6.0);
            else if (Math.Abs(max - g) < 1e-9)
                h = 60.0 * (((b - r) / delta) + 2.0);
            else
                h = 60.0 * (((r - g) / delta) + 4.0);

            if (h < 0)
                h += 360.0;

            return (h, s * 100.0, l * 100.0);
        }

        /// <summary>
        /// 只取色相（0–360）。用于「拿当前强调色反推滑杆位置」。
        /// </summary>
        public static double HueOf(Color c)
        {
            (double h, double s, _) = ToHsl(c);
            // 灰阶没有色相信息，返回 0 会让滑杆跳到最左。这里返回 195（默认青的色相），
            // 让「强调色是灰的」这种情况下滑杆落在视觉合理的位置。
            return s <= double.Epsilon ? 195.0 : h;
        }

        /// <summary>按滑杆语义（固定 S/L）把色相转成 #RRGGBB 字符串。</summary>
        public static string HexFromHue(double hue) =>
            ToHex(FromHsl(hue, DefaultSaturation, DefaultLightness));

        /// <summary>Color → #RRGGBB（大写，无 Alpha）。</summary>
        public static string ToHex(Color c) => string.Format(
            CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);

        /// <summary>百分比分量转字节，四舍五入并夹在 0–255。</summary>
        private static byte ToByte(double unit) => (byte)Math.Round(Clamp(unit, 0.0, 1.0) * 255.0);

        private static double Clamp(double v, double min, double max) =>
            v < min ? min : (v > max ? max : v);
    }
}
