using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using Microsoft.Win32;
using Zenwik.Helpers;
using Zenwik.Services;

namespace Zenwik.Views
{
    /// <summary>
    /// 通用页：样式（主题 + 强调色）/ 诊断（导出日志 + SMU 自检）/ 关于。
    ///
    /// 【初始化顺序不可调换】
    ///   1. 读 ui.json，把控件设成用户保存的值
    ///   2. 最后才挂 SelectionChanged
    /// 反过来会在打开页面的瞬间把用户的主题重置回默认值 ——
    /// 给 ComboBox 赋值本身就会触发 SelectionChanged。
    ///
    /// 这一页不持有任何硬件状态，只读写 ui.json 并通过 <see cref="ThemeService"/> 下发。
    /// </summary>
    public partial class GeneralPage : UserControl
    {
        public const string PageKey = "general";

        private const string DefaultAccent = "#38BDF8";

        /// <summary>屏蔽回调：程序化赋值不算用户操作。</summary>
        private bool _suppress;

        /// <summary>初始化完成后才允许事件回调工作。</summary>
        private bool _initialized;

        public GeneralPage()
        {
            InitializeComponent();

            // ↓↓↓ 顺序不可调换 ↓↓↓
            LoadSettings();
            AttachHandlers();
            ShowVersion();
        }

        // ==================== 读取 / 回写 ====================

        private void LoadSettings() => Suppressed(() =>
        {
            UiSettings settings = UiSettingsStore.Settings;

            string themeTag = settings.ThemeMode switch
            {
                AppThemeMode.Light => "light",
                AppThemeMode.Dark => "dark",
                _ => "system"
            };
            SelectByTag(ThemeCombo, themeTag);

            SelectByTag(AccentModeCombo, settings.FollowSystemAccent ? "follow" : "custom");
            CustomAccentPanel.Visibility = settings.FollowSystemAccent
                ? Visibility.Collapsed
                : Visibility.Visible;

            // 输入框回显「当前真正生效」的颜色：跟随时是系统强调色，自定义时是保存值。
            string hex = CurrentAccentHex();
            AccentHexBox.Text = hex;
            SyncHueSliderFromHex(hex);
            UpdateColorPreview(hex);
        });

        private void AttachHandlers()
        {
            ThemeCombo.SelectionChanged += ThemeCombo_SelectionChanged;
            AccentModeCombo.SelectionChanged += AccentModeCombo_SelectionChanged;
            _initialized = true;
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            foreach (object item in combo.Items)
            {
                if (item is ComboBoxItem ci && ci.Tag is string t && t == tag)
                {
                    combo.SelectedItem = ci;
                    return;
                }
            }
        }

        private static string SelectedTag(ComboBox combo)
            => combo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag ? tag : string.Empty;

        private static string CurrentAccentHex()
        {
            UiSettings s = UiSettingsStore.Settings;
            Color c = s.FollowSystemAccent ? ThemeService.AccentColor : ParseAccent(s.CustomAccentHex);

            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private static Color ParseAccent(string? hex)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(
                    string.IsNullOrWhiteSpace(hex) ? DefaultAccent : hex);
            }
            catch
            {
                return (Color)ColorConverter.ConvertFromString(DefaultAccent);
            }
        }

        /// <summary>把输入框、色相滑杆、色块预览同步到「当前真正生效」的颜色。</summary>
        private void SyncAccentUi() => Suppressed(() =>
        {
            string hex = CurrentAccentHex();
            AccentHexBox.Text = hex;
            SyncHueSliderFromHex(hex);
            UpdateColorPreview(hex);
        });

        // ==================== 色相选择器 ====================

        /// <summary>
        /// 把滑杆拨到指定颜色对应的色相位置。
        ///
        /// 必须「抑制」：HueSlider.Value 一改就触发 ValueChanged，
        /// 而那个处理器会去写盘。初始化 / 主题切换回填时如果不管，
        /// 会把「跟随系统」状态意外改成「自定义」。
        /// </summary>
        private void SyncHueSliderFromHex(string hex)
        {
            if (!TryParseColor(hex, out Color color))
                return;

            double hue = ColorHsl.HueOf(color);

            // 只在真的需要时才赋值，避免每次刷新都把用户正在拖的滑块踢回原位
            if (Math.Abs(HueSlider.Value - hue) > 0.5)
                HueSlider.Value = hue;
        }

        private void UpdateColorPreview(string hex)
        {
            if (!TryParseColor(hex, out Color color))
                return;

            SolidColorBrush brush = new(color);
            brush.Freeze();

            ColorPreviewBorder.Background = brush;
            ColorPreviewGlow.Color = color;
        }

        private static bool TryParseColor(string? hex, out Color color)
        {
            color = Colors.Transparent;
            if (string.IsNullOrWhiteSpace(hex))
                return false;

            try
            {
                if (ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                {
                    color = c;
                    return true;
                }
            }
            catch (FormatException)
            {
                // 用户正在输入中间态（比如只敲了 "#60"），解析失败是正常的
            }
            catch (NotSupportedException)
            {
            }

            return false;
        }

        /// <summary>
        /// 滑杆拖动：实时改色。
        ///
        /// 「拖一下就写一次配置」会把磁盘敲出几百次无效写入，所以拆成
        /// 「拖动中只改视觉、松手才落盘」。判据是 Thumb 是否被捕获。
        /// </summary>
        private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppress || !_initialized)
                return;

            string hex = ColorHsl.HexFromHue(e.NewValue);

            Suppressed(() =>
            {
                AccentHexBox.Text = hex;
                UpdateColorPreview(hex);
            });

            // 拖动中：把主题画刷换掉，用户能看到整个界面的强调色在跟着变，但不写盘
            if (HueSlider.IsMouseCaptureWithin)
            {
                ThemeService.Apply(ThemeService.Mode, false, hex);
                return;
            }

            ApplyCustomAccent(hex);
        }

        private void AccentHexBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter)
                return;

            ApplyAccentBtn_Click(sender, e);
            e.Handled = true;
        }

        // ==================== 下拉框 ====================

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || _suppress)
                return;

            AppThemeMode mode = SelectedTag(ThemeCombo) switch
            {
                "light" => AppThemeMode.Light,
                "dark" => AppThemeMode.Dark,
                _ => AppThemeMode.System
            };

            UiSettingsStore.Settings.ThemeMode = mode;
            ThemeService.ApplyAndSave(mode, ThemeService.FollowSystemAccent,
                                      UiSettingsStore.Settings.CustomAccentHex);
            LogService.Info($"主题模式：{mode}");

            // 跟随系统时强调色会变成系统色，输入框与滑块要跟着变
            SyncAccentUi();
        }

        private void AccentModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || _suppress)
                return;

            bool follow = SelectedTag(AccentModeCombo) != "custom";
            CustomAccentPanel.Visibility = follow ? Visibility.Collapsed : Visibility.Visible;

            ThemeService.ApplyAndSave(ThemeService.Mode, follow,
                                      UiSettingsStore.Settings.CustomAccentHex);

            SyncAccentUi();

            LogService.Info(follow ? "强调色：跟随系统" : "强调色：自定义");
        }

        /// <summary>「应用颜色」：接受用户手输的色值，输了不合法就明确提示。</summary>
        private void ApplyAccentBtn_Click(object sender, RoutedEventArgs e)
        {
            string raw = (AccentHexBox.Text ?? string.Empty).Trim();
            if (!TryNormalizeHex(raw, out string hex, out string err))
            {
                ToastService.Warning(err);
                AccentHexBox.Focus();
                AccentHexBox.SelectAll();
                return;
            }

            ApplyCustomAccent(hex);
        }

        private void ApplyCustomAccent(string hex)
        {
            if (!TryNormalizeHex(hex, out string norm, out _))
                norm = DefaultAccent;

            Suppressed(() =>
            {
                SelectByTag(AccentModeCombo, "custom");
                CustomAccentPanel.Visibility = Visibility.Visible;
                AccentHexBox.Text = norm;
                SyncHueSliderFromHex(norm);
                UpdateColorPreview(norm);
            });

            ThemeService.ApplyAndSave(ThemeService.Mode, false, norm);
            LogService.Info($"自定义强调色：{norm}");
        }

        /// <summary>
        /// 把用户输入规范成 #RRGGBB（大写）。接受 #RGB / #RRGGBB / #AARRGGBB / RRGGBB；
        /// 8 位时丢掉 Alpha 通道。只做格式校验，不限制色相 —— 这是「自定义」该有的自由度。
        /// </summary>
        private static bool TryNormalizeHex(string input, out string hex, out string error)
        {
            hex = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(input))
            {
                error = "请输入颜色值，例如 #38BDF8";
                return false;
            }

            string s = input.Trim().TrimStart('#');

            if (s.Length == 3)
            {
                string expanded = string.Empty;
                foreach (char c in s)
                {
                    if (!Uri.IsHexDigit(c))
                    {
                        error = $"「{input}」不是合法颜色值";
                        return false;
                    }

                    expanded += new string(c, 2);
                }

                s = expanded;
            }

            if (s.Length == 8)
                s = s.Substring(2);      // 丢掉 Alpha

            if (s.Length != 6)
            {
                error = $"「{input}」不是合法颜色值（需要 #RRGGBB）";
                return false;
            }

            foreach (char c in s)
            {
                if (!Uri.IsHexDigit(c))
                {
                    error = $"「{input}」不是合法颜色值";
                    return false;
                }
            }

            hex = "#" + s.ToUpperInvariant();
            return true;
        }

        // ==================== 诊断 ====================

        /// <summary>
        /// 导出本次会话日志到用户选的位置。
        ///
        /// 为什么需要它：日志文件是滚动的（按天切 + 上限清理），
        /// 用户想保留一份「现在这个状态」的记录时，得有个出口把它拿出来。
        /// 写盘在后台线程做，几百 KB 到几 MB 的 I/O 不该冻住界面。
        /// </summary>
        private async void ExportLogButton_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dialog = new()
            {
                Title = "导出日志",
                Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
                DefaultExt = ".log",
                FileName = $"zenforge-{DateTime.Now:yyyyMMdd-HHmmss}.log"
            };

            if (dialog.ShowDialog() != true)
                return;

            ExportLogButton.IsEnabled = false;
            ExportLogButton.Content = "导出中…";

            try
            {
                string target = dialog.FileName;
                await Task.Run(() => WriteLogSnapshot(target));

                LogService.Info($"已导出日志到 {target}");
                RevealInExplorer(target);
                ToastService.Success("日志已导出");
            }
            catch (Exception ex)
            {
                LogService.Error($"导出日志失败：{ex.Message}");
                ToastService.Error($"导出失败：{ex.Message}");
            }
            finally
            {
                ExportLogButton.Content = "导出";
                ExportLogButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 把内存里的日志快照写成文件。
        ///
        /// 必须在锁内先把条目复制出来再写：LogService.Entries 是后台线程在写的
        /// 集合，边遍历边写会撞上 InvalidOperationException。
        /// </summary>
        private static void WriteLogSnapshot(string path)
        {
            LogEntry[] snapshot = LogService.Snapshot();

            StringBuilder sb = new();
            sb.AppendLine($"Zenwik 日志导出 · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 共 {snapshot.Length} 条");
            sb.AppendLine();

            foreach (LogEntry entry in snapshot)
                sb.AppendLine($"{entry.Time:HH:mm:ss.fff} [{entry.LevelText}] {entry.Message}");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// SMU 自检：跑一遍只读 + 回滚式自检，把结果写成 txt 并打开所在目录。
        ///
        /// CO 被整片拒绝 / FMax 回读为 0 这类问题，光看应用日志分不清是
        /// 「拓扑拆分不对」「SMU 不支持该消息」还是「权限不足」，自检会逐项打出来。
        /// </summary>
        private async void SmuDiagButton_Click(object sender, RoutedEventArgs e)
        {
            SmuDiagButton.IsEnabled = false;
            SmuDiagButton.Content = "正在自检…";

            string? path;
            try
            {
                path = await Task.Run(SmuDiagnostics.Run);
            }
            catch (Exception ex)
            {
                LogService.Error($"SMU 自检失败：{ex}");
                ToastService.Error($"自检过程中出错：{ex.Message}");
                return;
            }
            finally
            {
                SmuDiagButton.Content = "运行";
                SmuDiagButton.IsEnabled = true;
            }

            if (string.IsNullOrEmpty(path))
            {
                ToastService.Warning("自检未能生成报告文件");
                return;
            }

            LogService.Info($"SMU 自检完成，报告：{path}");
            RevealInExplorer(path);
            ToastService.Success("SMU 自检完成，报告已在资源管理器中打开");
        }

        private static void RevealInExplorer(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打不开资源管理器不影响文件已生成
            }
        }

        // ==================== 关于 ====================

        private void GitHubLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogService.Warn($"打开 GitHub 链接失败：{ex.Message}");
            }

            e.Handled = true;
        }

        /// <summary>物理文件版本号才是用户该看的那个（显示在「关于」卡里）。</summary>
        private void ShowVersion()
        {
            try
            {
                string exe = Environment.ProcessPath ?? string.Empty;
                string text = File.Exists(exe)
                    ? FileVersionInfo.GetVersionInfo(exe).FileVersion ?? string.Empty
                    : string.Empty;

                string[] parts = text.Split('.');
                string version = parts.Length >= 3
                    ? string.Join(".", parts[0], parts[1], parts[2])
                    : text;

                AboutVersionText.Text = $"版本 v{version} · MIT License";
            }
            catch
            {
                AboutVersionText.Text = "MIT License";
            }
        }

        private void Suppressed(Action action)
        {
            _suppress = true;
            try { action(); }
            finally { _suppress = false; }
        }
    }
}
