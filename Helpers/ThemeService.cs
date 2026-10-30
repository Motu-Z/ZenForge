using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ZenForge.Services;

namespace ZenForge.Helpers
{
    /// <summary>
    /// 主题服务：深/浅色整册切换 + 强调色动态改写 + 跟随系统（2 秒轮询注册表，零外部依赖）。
    /// </summary>
    public static class ThemeService
    {
        private const string PaletteDark = "Themes/Palette.Dark.xaml";
        private const string PaletteLight = "Themes/Palette.Light.xaml";
        private const string DefaultAccentHex = "#38BDF8";
        private const string FallbackSystemAccentHex = "#0078D4";

        private static ResourceDictionary? _accentDictionary;
        private static ManagedTimer? _systemMonitorTimer;
        private static bool _lastKnownDark = true;
        private static Color _lastKnownSystemAccent;

        public static AppThemeMode Mode { get; private set; } = AppThemeMode.System;

        public static bool FollowSystemAccent { get; private set; } = true;

        public static Color AccentColor { get; private set; }

        public static bool IsDark { get; private set; } = true;

        /// <summary>主题或强调色发生变化时触发（用于刷新窗口标题栏等）。</summary>
        public static event EventHandler? ThemeChanged;

        public static void Initialize()
        {
            Collection<ResourceDictionary>? dictionaries = Application.Current?.Resources.MergedDictionaries;
            if (dictionaries != null)
            {
                _accentDictionary = null;
                foreach (ResourceDictionary dictionary in dictionaries)
                {
                    if (dictionary.Source != null &&
                        dictionary.Source.OriginalString.Contains("Accent.xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        _accentDictionary = dictionary;
                        break;
                    }
                }
            }

            ApplyFromSettings();
        }

        public static void ApplyFromSettings()
        {
            UiSettings settings = UiSettingsStore.Settings;
            Apply(settings.ThemeMode, settings.FollowSystemAccent, settings.CustomAccentHex);
        }

        /// <summary>应用主题并持久化。</summary>
        public static void ApplyAndSave(AppThemeMode mode, bool followSystemAccent, string? customAccentHex)
        {
            UiSettingsStore.Settings.ThemeMode = mode;
            UiSettingsStore.Settings.FollowSystemAccent = followSystemAccent;
            UiSettingsStore.Settings.CustomAccentHex = string.IsNullOrWhiteSpace(customAccentHex) ? DefaultAccentHex : customAccentHex;
            UiSettingsStore.Save();

            Apply(mode, followSystemAccent, customAccentHex);
        }

        public static void Apply(AppThemeMode mode, bool followSystemAccent, string? customAccentHex)
        {
            Mode = mode;
            FollowSystemAccent = followSystemAccent;

            bool dark = ResolveDark(mode);
            IsDark = dark;
            SwapPalette(dark ? PaletteDark : PaletteLight);

            Color accent = followSystemAccent
                ? GetSystemAccentColor()
                : ResolveCustomAccent(customAccentHex);

            if (followSystemAccent)
                _lastKnownSystemAccent = accent;

            AccentColor = accent;
            SetAccentBrushes(accent);

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>自定义色解析失败或没给，一律回落到默认青，而不是系统强调色那套兜底。</summary>
        private static Color ResolveCustomAccent(string? customAccentHex)
        {
            if (!string.IsNullOrWhiteSpace(customAccentHex) &&
                ColorConverter.ConvertFromString(customAccentHex) is Color parsed)
            {
                return parsed;
            }

            return (Color)ColorConverter.ConvertFromString(DefaultAccentHex);
        }

        /// <summary>
        /// 启动系统主题/强调色监视。Windows 设置变更后最多 2 秒内自动跟随。
        /// （后续可替换为 SystemEvents.UserPreferenceChanged，轮询方案零 API 风险。）
        ///
        /// 【为什么走 PageTimerManager + HostScope】
        /// 2 秒一拍的轮询是「后台 CPU 空转」最大的单点来源：它每拍都要
        /// 读注册表 + 读系统强调色，而且跑在 UI 线程上。
        /// 交给 PageTimerManager 之后，窗口隐藏 / 最小化 / 收进托盘时
        /// 它会被自动挂起，回到界面再自动恢复 —— 恢复时重新对表，
        /// 期间错过的主题变化照样会被补上。
        /// </summary>
        public static void StartSystemMonitor()
        {
            _lastKnownDark = IsDark;

            // 重复调用会丢掉旧定时器：ManagedTimer 一旦 Create 就登记进
            // PageTimerManager 的静态表，不 Dispose 就永远留在那里 ——
            // 每次从托盘回来都建一个新的，等于越攒越多。
            _systemMonitorTimer?.Dispose();

            _systemMonitorTimer = PageTimerManager.Create(
                PageTimerManager.HostScope,
                TimeSpan.FromSeconds(2),
                (_, _) => OnMonitorTick());

            _systemMonitorTimer.Start();
        }

        private static void OnMonitorTick()
        {
            bool dark = ResolveDark(Mode);
            if (dark == _lastKnownDark)
            {
                CheckSystemAccent();
                return;
            }

            _lastKnownDark = dark;
            IsDark = dark;
            SwapPalette(dark ? PaletteDark : PaletteLight);
            ThemeChanged?.Invoke(null, EventArgs.Empty);

            CheckSystemAccent();
        }

        private static void CheckSystemAccent()
        {
            if (!FollowSystemAccent)
                return;

            Color system = GetSystemAccentColor();
            if (system == _lastKnownSystemAccent)
                return;

            _lastKnownSystemAccent = system;
            AccentColor = system;
            SetAccentBrushes(system);
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// 停掉系统主题 / 强调色轮询。
        ///
        /// 收进托盘之后没人看界面，让它每 2 秒唤醒一次 UI 线程纯属浪费。
        /// 回到界面时调 StartSystemMonitor() 会重新对表，期间错过的主题变化也会被补上。
        /// </summary>
        public static void StopSystemMonitor() => _systemMonitorTimer?.Stop();

        private static bool ResolveDark(AppThemeMode mode) => mode switch
        {
            AppThemeMode.Light => false,
            AppThemeMode.Dark => true,
            _ => !IsSystemLightTheme()
        };

        private static bool IsSystemLightTheme()
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is int i && i == 1;
        }

        /// <summary>读取 Windows 的 DWM 强调色（DWORD 为 ABGR 小端存放）。</summary>
        private static Color GetSystemAccentColor()
        {
            object? value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
            if (value is int dwm)
            {
                byte r = (byte)(dwm & 0xFF);
                byte g = (byte)((dwm >> 8) & 0xFF);
                byte b = (byte)((dwm >> 16) & 0xFF);
                return Color.FromRgb(r, g, b);
            }

            return (Color)ColorConverter.ConvertFromString(FallbackSystemAccentHex);
        }

        private static void SwapPalette(string source)
        {
            if (Application.Current == null)
                return;

            Collection<ResourceDictionary> dictionaries = Application.Current.Resources.MergedDictionaries;

            ResourceDictionary? existing = null;
            foreach (ResourceDictionary dictionary in dictionaries)
            {
                if (dictionary.Source != null &&
                    dictionary.Source.OriginalString.Contains("Palette.", StringComparison.OrdinalIgnoreCase))
                {
                    existing = dictionary;
                    break;
                }
            }

            ResourceDictionary fresh = new() { Source = new Uri(source, UriKind.Relative) };
            if (existing != null)
                dictionaries[dictionaries.IndexOf(existing)] = fresh;
            else
                dictionaries.Insert(1, fresh);

            ApplySystemColorOverrides(source == PaletteDark);
        }

        // App.xaml 为 SystemColors 准备了一组兜底画刷，用来压住 ComboBox 下拉弹层、
        // 右键菜单这类不走 DynamicResource 的系统部件，避免深色界面里冒出白底。
        //
        // 原实现在切换主题时从未同步过这组画刷，于是切到浅色主题后，上述部件
        // 仍停留在启动时那一版深色，形成明显的「花斑」。
        //
        // 关键：资源键必须是 SystemColors.XxxKey 这个 ResourceKey 实例本身。
        // x:Key="{x:Static SystemColors.WindowBrushKey}" 存入字典时用的就是该对象；
        // 若拿同名字符串去赋值，只会新建一个无人引用的键，同步不会生效。
        private static void ApplySystemColorOverrides(bool dark)
        {
            if (Application.Current == null)
                return;

            ResourceDictionary res = Application.Current.Resources;

            res[SystemColors.WindowBrushKey] = Frozen(dark ? Color.FromRgb(0x12, 0x12, 0x14) : Color.FromRgb(0xF2, 0xF2, 0xF4));
            res[SystemColors.ControlBrushKey] = Frozen(dark ? Color.FromRgb(0x18, 0x18, 0x1A) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            res[SystemColors.ControlTextBrushKey] = Frozen(dark ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1C, 0x1C, 0x1E));
            res[SystemColors.MenuBrushKey] = Frozen(dark ? Color.FromRgb(0x12, 0x12, 0x14) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            res[SystemColors.MenuTextBrushKey] = Frozen(dark ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1C, 0x1C, 0x1E));
            res[SystemColors.HighlightBrushKey] = Frozen(ParseAccentHex(FallbackSystemAccentHex));
            res[SystemColors.HighlightTextBrushKey] = Frozen(dark ? Color.FromRgb(0x10, 0x10, 0x10) : Color.FromRgb(0xFF, 0xFF, 0xFF));
        }

        private static SolidColorBrush Frozen(Color color)
        {
            SolidColorBrush brush = new(color);
            brush.Freeze();
            return brush;
        }

        private static Color ParseAccentHex(string hex)
        {
            if (ColorConverter.ConvertFromString(hex) is Color color)
                return color;
            return (Color)ColorConverter.ConvertFromString(FallbackSystemAccentHex);
        }

        /// <summary>
        /// 允许被强调色动态改写的资源键**白名单**。
        ///
        /// 【为什么需要白名单】
        /// 强调色是「交互语义」：按钮、滑块、选中高亮、焦点边框。
        /// 语义色是「状态语义」：Log.Info / Log.Warn / Log.Error / Log.Critical、
        /// State.*、Danger —— 它们表达「这条日志有多严重」，与用户喜欢什么颜色无关。
        ///
        /// 用户把强调色改成红色，错误日志必须还是红的；改成黄色，警告必须还是黄的。
        /// 一旦语义色被强调色覆盖，界面语言就崩了：红色不再等于出错。
        ///
        /// 因此本方法只、且只能写下面这 5 个键（全部位于 Accent.xaml），
        /// 任何新增的改写点都必须先加进白名单，否则 GuardResourceKey 会抛异常
        /// —— 让「手滑改了语义色」在开发期就炸掉，而不是发布后让用户看见
        /// 一堆跟着主题变色的告警。
        /// </summary>
        private static readonly string[] AccentOwnedKeys =
        {
            "AccentBrush",
            "AccentHoverBrush",
            "AccentPressedBrush",
            "AccentSoftBrush",
            "AccentTextBrush",
        };

        /// <summary>
        /// 语义色键：这些键**任何情况下**都不许被 SetAccentBrushes 触碰。
        /// 与白名单互为双保险 —— 白名单负责「只允许这些」，
        /// 这里负责在日志里大喊「有人动了不该动的东西」。
        /// </summary>
        private static readonly string[] SemanticColorKeys =
        {
            "Log.Info", "Log.Warn", "Log.Error", "Log.Critical",
            "Log.InfoTint", "Log.WarnTint", "Log.ErrorTint", "Log.CriticalTint",
            "State.Info", "State.Success", "State.Warning", "State.Danger",
            "Danger", "DangerSoft",
        };

        private static void SetAccentBrushes(Color color)
        {
            if (_accentDictionary == null)
                return;

            // 强调色前景原本固定 White：青色系强调色上白字对比度仅约 2.2:1，明显发虚，
            // 而列表选中态却在用近黑色，同一强调色出现两套前景。
            // 改为按感知亮度自动择黑白，亮色强调配黑字、深色强调配白字。
            //
            // 全部经由 WriteAccent 写入：它同时校验白名单与语义色黑名单，
            // 保证这里永远不可能动到 Log.* / State.* / Danger*。
            WriteAccent("AccentBrush", Frozen(color));
            WriteAccent("AccentHoverBrush", Frozen(Adjust(color, 1.12f)));
            WriteAccent("AccentPressedBrush", Frozen(Adjust(color, 0.88f)));
            // 15% Alpha（0x26 = 38/255 ≈ 14.9%）：v2 强调色方案里，淡底只做「极轻的
            // 区域标记」，压得太深会和实底强调色抢视觉重量。深色册与浅色册共用此算法。
            WriteAccent("AccentSoftBrush", Frozen(Color.FromArgb(0x26, color.R, color.G, color.B)));
            WriteAccent("AccentTextBrush", Frozen(NeedsDarkForeground(color) ? Colors.Black : Colors.White));
        }

        /// <summary>
        /// 向强调色字典写入单个键，写入前做双重校验。
        ///
        /// 这是「色彩解耦」的执行点：强调色体系与语义色体系在此处分道扬镳，
        /// 任何试图把语义色塞进强调色刷新流程的改动都会在这里被拦下。
        /// </summary>
        private static void WriteAccent(string key, SolidColorBrush brush)
        {
            // 第一道：语义色黑名单。先查这个，因为它的错误性质更严重。
            if (Array.IndexOf(SemanticColorKeys, key) >= 0)
            {
                throw new InvalidOperationException(
                    $"禁止用强调色覆盖语义色 \"{key}\"：语义色必须固定，不随主题变化。");
            }

            // 第二道：白名单。不在名单里的键一律拒绝，防止将来新增键时绕过规则。
            if (Array.IndexOf(AccentOwnedKeys, key) < 0)
            {
                throw new InvalidOperationException(
                    $"\"{key}\" 不在强调色白名单内。新增强调色键请先更新 AccentOwnedKeys。");
            }

            _accentDictionary![key] = brush;
        }

        /// <summary>
        /// 供设置页 / 诊断使用：某个资源键是否属于语义色（不随强调色变化）。
        /// </summary>
        public static bool IsSemanticColorKey(string key) => Array.IndexOf(SemanticColorKeys, key) >= 0;

        // 强调色之上该配深色前景还是浅色前景。
        // 按 WCAG 相对亮度判断：内置候选色中 青/橙/绿 → 黑字，蓝/紫/红 → 白字。
        private static bool NeedsDarkForeground(Color c)
        {
            return RelativeLuminance(c) > 0.25;
        }

        // WCAG 2.x 相对亮度
        private static double RelativeLuminance(Color c)
        {
            return 0.2126 * Linearize(c.R / 255.0)
                 + 0.7152 * Linearize(c.G / 255.0)
                 + 0.0722 * Linearize(c.B / 255.0);
        }

        // sRGB 分量线性化
        private static double Linearize(double v)
        {
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        private static Color Adjust(Color color, float factor)
        {
            byte Clamp(int value) => (byte)Math.Max(0, Math.Min(255, value));
            return Color.FromRgb(
                Clamp((int)(color.R * factor)),
                Clamp((int)(color.G * factor)),
                Clamp((int)(color.B * factor)));
        }
    }
}
