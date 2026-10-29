using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ZenForge.Helpers
{
    /// <summary>
    /// 主题服务：深/浅色整册切换 + 强调色动态改写 + 跟随系统（2 秒轮询注册表，零外部依赖）。
    /// </summary>
    public static class ThemeService
    {
        private const string PaletteDark = "Themes/Palette.Dark.xaml";
        private const string PaletteLight = "Themes/Palette.Light.xaml";
        private const string DefaultAccentHex = "#00B4D8";
        private const string FallbackSystemAccentHex = "#0078D4";

        private static ResourceDictionary? _accentDictionary;
        private static DispatcherTimer? _systemMonitorTimer;
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

            Color accent;
            if (followSystemAccent)
            {
                accent = GetSystemAccentColor();
            }
            else if (!string.IsNullOrWhiteSpace(customAccentHex))
            {
                try
                {
                    accent = (Color)ColorConverter.ConvertFromString(customAccentHex);
                }
                catch
                {
                    accent = (Color)ColorConverter.ConvertFromString(DefaultAccentHex);
                }
            }
            else
            {
                accent = (Color)ColorConverter.ConvertFromString(DefaultAccentHex);
            }

            if (followSystemAccent)
                _lastKnownSystemAccent = accent;

            AccentColor = accent;
            SetAccentBrushes(accent);

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// 启动系统主题/强调色监视。Windows 设置变更后最多 2 秒内自动跟随。
        /// （后续可替换为 SystemEvents.UserPreferenceChanged，轮询方案零 API 风险。）
        /// </summary>
        public static void StartSystemMonitor()
        {
            _lastKnownDark = IsDark;

            // 重复调用会丢掉旧定时器。DispatcherTimer 一旦 Start 就被 Dispatcher 持有，
            // 不显式 Stop 就会一直 tick —— 每次从托盘回来都建一个新的，等于越攒越多。
            if (_systemMonitorTimer != null)
                _systemMonitorTimer.Stop();

            _systemMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _systemMonitorTimer.Tick += (_, _) =>
            {
                bool dark = ResolveDark(Mode);
                if (dark != _lastKnownDark)
                {
                    _lastKnownDark = dark;
                    IsDark = dark;
                    SwapPalette(dark ? PaletteDark : PaletteLight);
                    ThemeChanged?.Invoke(null, EventArgs.Empty);
                }

                if (FollowSystemAccent)
                {
                    Color system = GetSystemAccentColor();
                    if (system != _lastKnownSystemAccent)
                    {
                        _lastKnownSystemAccent = system;
                        AccentColor = system;
                        SetAccentBrushes(system);
                        ThemeChanged?.Invoke(null, EventArgs.Empty);
                    }
                }
            };
            _systemMonitorTimer.Start();
        }

        /// <summary>
        /// 停掉系统主题 / 强调色轮询。
        ///
        /// 收进托盘之后没人看界面，让它每 2 秒唤醒一次 UI 线程纯属浪费。
        /// 回到界面时调 StartSystemMonitor() 会重新对表，期间错过的主题变化也会被补上。
        /// </summary>
        public static void StopSystemMonitor()
        {
            try
            {
                _systemMonitorTimer?.Stop();
            }
            catch
            {
                // 忽略
            }
        }

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

        // ==================================================================
        // App.xaml 为 SystemColors 准备了一组兜底画刷，用来压住 ComboBox 下拉弹层、
        // 右键菜单这类不走 DynamicResource 的系统部件，避免深色界面里冒出白底。
        //
        // 原实现在切换主题时从未同步过这组画刷，于是切到浅色主题后，上述部件
        // 仍停留在启动时那一版深色，形成明显的「花斑」。
        //
        // 关键：资源键必须是 SystemColors.XxxKey 这个 ResourceKey 实例本身。
        // x:Key="{x:Static SystemColors.WindowBrushKey}" 存入字典时用的就是该对象；
        // 若拿同名字符串去赋值，只会新建一个无人引用的键，同步不会生效。
        // ==================================================================
        private static void ApplySystemColorOverrides(bool dark)
        {
            if (Application.Current == null)
                return;

            ResourceDictionary res = Application.Current.Resources;

            res[SystemColors.WindowBrushKey] = Frozen(dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xF2, 0xF2, 0xF4));
            res[SystemColors.ControlBrushKey] = Frozen(dark ? Color.FromRgb(0x25, 0x25, 0x26) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            res[SystemColors.ControlTextBrushKey] = Frozen(dark ? Color.FromRgb(0xF0, 0xF0, 0xF0) : Color.FromRgb(0x1C, 0x1C, 0x1E));
            res[SystemColors.MenuBrushKey] = Frozen(dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            res[SystemColors.MenuTextBrushKey] = Frozen(dark ? Color.FromRgb(0xF0, 0xF0, 0xF0) : Color.FromRgb(0x1C, 0x1C, 0x1E));
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

        private static void SetAccentBrushes(Color color)
        {
            if (_accentDictionary == null)
                return;

            // 强调色前景原本固定 White：青色系强调色上白字对比度仅约 2.2:1，明显发虚，
            // 而列表选中态却在用近黑色，同一强调色出现两套前景。
            // 改为按感知亮度自动择黑白，亮色强调配黑字、深色强调配白字。
            _accentDictionary["AccentBrush"] = Frozen(color);
            _accentDictionary["AccentHoverBrush"] = Frozen(Adjust(color, 1.12f));
            _accentDictionary["AccentPressedBrush"] = Frozen(Adjust(color, 0.88f));
            _accentDictionary["AccentSoftBrush"] = Frozen(Color.FromArgb(0x2E, color.R, color.G, color.B));
            _accentDictionary["AccentTextBrush"] = Frozen(NeedsDarkForeground(color) ? Colors.Black : Colors.White);
        }

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
