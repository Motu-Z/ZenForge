using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZenForge.Helpers;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// SettingsPage：外观设置 + 启动开关 + 全局重置。
    ///
    /// 「外观」区按 v12 稿：两行下拉（主题模式 / 强调色）+ 一个色相选择器。
    ///   · 两个下拉框都用 Tag 驱动（system/dark/light、follow/custom）
    ///   · 色相选择器 = 当前色预览 + 彩虹滑杆 + 色值输入框 + 应用按钮，同一行
    ///   · 滑杆实时改色（拖动即生效），输入框回车 / 点「应用」也会切到自定义
    ///   · 原来那 8 个预设色圆点已移除：滑杆本身就是全部色相，圆点是冗余
    ///
    /// 初始化顺序（关键，修复“打开设置页主题被重置”）：
    ///   1. LoadSettings()   —— 读 ui.json，把控件设成用户保存的值
    ///   2. AttachHandlers() —— 最后才挂 SelectionChanged，杜绝初始化反向覆盖
    /// </summary>
    public partial class SettingsPage : Page
    {
        private const string DefaultAccent = "#60CDFF";

        private bool _suppress;
        private bool _initialized;

        /// <summary>在屏蔽事件回调的前提下改控件状态，避免初始化 / 回滚被当成用户操作。</summary>
        private void Suppressed(Action action)
        {
            _suppress = true;
            try { action(); }
            finally { _suppress = false; }
        }

        /// <summary>程序化拨动开关（不触发 Click）。</summary>
        private void SetToggle(System.Windows.Controls.Primitives.ToggleButton toggle, bool value)
            => Suppressed(() => toggle.IsChecked = value);

        public SettingsPage()
        {
            InitializeComponent();

            // ↓↓↓ 顺序不可调换 ↓↓↓
            LoadSettings();
            AttachHandlers();

            // 订阅主题变化以刷新「当前生效」相关视觉。
            // 必须 Loaded / Unloaded 成对：若只在构造函数订阅、Unloaded 里解绑，
            // 页面一旦被 Frame 缓存复用，再次进入就再也收不到主题变更（换肤后
            // 这一页的色块不跟着变）；反过来只订阅不解绑则会累积回调。
            Loaded += (_, _) =>
            {
                ThemeService.ThemeChanged -= OnThemeChanged;
                ThemeService.ThemeChanged += OnThemeChanged;
            };
            Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;

            // 页面渲染完成后再异步校准「开机自启」的真实状态（schtasks 查询较慢，不阻塞导航）
            Loaded += SettingsPage_Loaded;
        }

        /// <summary>
        /// 异步校准「开机静默自启」的真实状态。
        ///
        /// 为什么不能只信 ui.json：用户可能在「任务计划程序」里手动删掉了 ZenForgeAutoStart，
        /// 或换机器后配置被拷贝过来。必须以计划任务的实际存在性为准，否则界面会谎报"已开启"。
        /// </summary>
        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= SettingsPage_Loaded; // 只执行一次

            try
            {
                bool taskExists = await Task.Run(AutoStartService.IsEnabled);

                // 校准期间用户可能已经手动拨过开关，此时不要再覆盖他的操作
                if (_suppress)
                    return;

                _suppress = true;
                AutoStartToggle.IsChecked = taskExists;
                _suppress = false;

                UiSettings settings = UiSettingsStore.Settings;
                if (taskExists != settings.AutoStart)
                {
                    settings.AutoStart = taskExists;
                    UiSettingsStore.Save();
                    LogService.Info(taskExists
                        ? "已校准开机自启状态：计划任务存在，开关显示为开启"
                        : "已校准开机自启状态：计划任务不存在，开关已复位为关闭");
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"校准开机自启状态失败：{ex.Message}");
            }
        }

        // ==================== 外观：读取 / 回写 ====================

        /// <summary>第 2 步：用 ui.json 中保存的值初始化控件（此时还没有事件订阅）。</summary>
        private void LoadSettings() => Suppressed(() =>
        {
            UiSettings settings = UiSettingsStore.Settings;

            // 主题：跟随系统 / 深色 / 浅色（与 NvForge 同一套 Tag）
            string themeTag = settings.ThemeMode switch
            {
                AppThemeMode.Light => "light",
                AppThemeMode.Dark => "dark",
                _ => "system"
            };
            SelectByTag(ThemeCombo, themeTag);

            // 强调色：跟随系统 / 自定义
            SelectByTag(AccentModeCombo, settings.FollowSystemAccent ? "follow" : "custom");
            CustomAccentPanel.Visibility = settings.FollowSystemAccent
                ? Visibility.Collapsed
                : Visibility.Visible;

            // 色值输入框回显「当前真正生效」的颜色：
            // 跟随时是系统强调色，自定义时是保存的自定义色。
            string hex = CurrentAccentHex();
            AccentHexBox.Text = hex;
            SyncHueSliderFromHex(hex);
            UpdateColorPreview(hex);

            // ==================== 启动与后台开关回显 ====================
            // 布局阶段只用配置缓存回显，保证页面立即渲染不卡顿。
            // “开机自启”的真实状态（计划任务是否存在）稍后在 Loaded 里异步校准。
            AutoStartToggle.IsChecked = settings.AutoStart;
            ApplyTuningToggle.IsChecked = settings.ApplyTuningOnStartup;

            // 总闸与轻量模式：纯配置项，不存在“真实状态 ≠ 意愿值”的问题
            BackendServiceToggle.IsChecked = settings.BackendService;
            LightModeToggle.IsChecked = settings.LightMode;

            // 回显调度的**真实状态**而不是配置里的意愿值。
            // 两者会不一致：另一个实例占着全局互斥体时 Start() 会静默跳过，
            // 配置写着「开」实际一次都没绑 —— 以 IsRunning 为准，开关才不撒谎。
            ProcessWatchToggle.IsChecked = ProcessWatcher.IsRunning;

            ShowTrayToggle.IsChecked = settings.ShowTrayIcon;
            ErrorNotifyToggle.IsChecked = settings.NotifyOnError;

            // 最后再算一遍显隐：上面的 IsChecked 赋值不会触发 Click，
            // 但也不会自动刷新依赖行的可见性。
            RefreshRowVisibility();
        });

        /// <summary>第 3 步：初始化完成后才挂事件，杜绝初始化反向覆盖。</summary>
        private void AttachHandlers()
        {
            ThemeCombo.SelectionChanged += ThemeCombo_SelectionChanged;
            AccentModeCombo.SelectionChanged += AccentModeCombo_SelectionChanged;
            _initialized = true;
        }

        private void SelectByTag(ComboBox combo, string tag)
        {
            if (combo == null)
                return;

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
        {
            return combo?.SelectedItem is ComboBoxItem ci && ci.Tag is string tag ? tag : string.Empty;
        }

        /// <summary>当前真正生效的强调色（#RRGGBB 大写）。</summary>
        private static string CurrentAccentHex()
        {
            UiSettings s = UiSettingsStore.Settings;

            // 跟随时直接用 ThemeService 已解析好的系统强调色
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
        private void SyncAccentUi()
        {
            string hex = CurrentAccentHex();
            Suppressed(() =>
            {
                AccentHexBox.Text = hex;
                SyncHueSliderFromHex(hex);
                UpdateColorPreview(hex);
            });
        }

        // ==================== 外观：色相选择器 ====================

        /// <summary>
        /// 把滑杆拨到指定颜色对应的色相位置。
        ///
        /// 为什么必须「抑制」：HueSlider.Value 一改就会触发 ValueChanged，
        /// 而那个处理器会去写盘。初始化 / 主题切换回填时如果不管，会把
        /// 「跟随系统」状态意外改成「自定义」。
        /// 这里除了 _suppress 之外还要额外判一次——滑块拖到 195 附近时
        /// 由于浮点误差可能与当前值差 0.0001，ValueChanged 仍会触发，
        /// 所以回填完成后立刻把标志位放开，靠 _suppress 挡住这一轮。
        /// </summary>
        private void SyncHueSliderFromHex(string hex)
        {
            if (HueSlider == null || !TryParseColor(hex, out Color color))
                return;

            double hue = ColorHsl.HueOf(color);
            // 只在真的需要时才赋值，避免每次刷新都把用户正在拖的滑块踢回原位
            if (Math.Abs(HueSlider.Value - hue) > 0.5)
                HueSlider.Value = hue;
        }

        /// <summary>刷新色块预览的底色与外发光颜色。</summary>
        private void UpdateColorPreview(string hex)
        {
            if (!TryParseColor(hex, out Color color))
                return;

            SolidColorBrush brush = new(color);
            brush.Freeze();

            if (ColorPreviewBorder != null)
                ColorPreviewBorder.Background = brush;

            if (ColorPreviewGlow != null)
                ColorPreviewGlow.Color = color;
        }

        /// <summary>宽松解析色值；失败返回 false，不抛。</summary>
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
        /// 这里把「拖一下就写一次配置、刷一次主题」简化成「拖就刷主题，松手才落盘」：
        /// 拖动过程中 60 次 ValueChanged 若每次都写 json，会把磁盘敲出几百次无效写入。
        /// 判据是 Thumb 是否被捕获 —— 捕获中 = 用户还按着，先只改视觉；
        /// 松开时再走一次完整的 ApplyCustomAccent 落盘。
        /// </summary>
        private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppress || !_initialized)
                return;

            string hex = ColorHsl.HexFromHue(e.NewValue);

            // 滑块正在被拖动：只更新预览与输入框，不写盘
            bool dragging = HueSlider.IsMouseCaptureWithin;
            Suppressed(() =>
            {
                AccentHexBox.Text = hex;
                UpdateColorPreview(hex);
            });

            if (dragging)
            {
                // 拖动过程中就把主题画刷换掉，用户能看到整个界面的强调色在跟着变
                ThemeService.Apply(ThemeService.Mode, false, hex);
                return;
            }

            ApplyCustomAccent(hex);
        }

        /// <summary>输入框里按回车 = 点「应用」。</summary>
        private void AccentHexBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter)
                return;

            ApplyAccentBtn_Click(sender, e);
            e.Handled = true;
        }

        // ==================== 外观：事件 ====================

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

            // 跟随系统时强调色会变成系统色，色板选中环与输入框需要同步
            SyncAccentUi();
        }

        /// <summary>强调色模式切换：与「主题模式」完全同构的下拉框逻辑。</summary>
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

        /// <summary>
        /// 「应用颜色」：接受用户手输的色值。
        /// 支持 #RRGGBB / RRGGBB / #RGB / #AARRGGBB 几种常见写法，比用户更宽容一点 ——
        /// 输错了给明确提示，不静默吞掉。
        /// </summary>
        private void ApplyAccentBtn_Click(object sender, RoutedEventArgs e)
        {
            string raw = (AccentHexBox.Text ?? string.Empty).Trim();
            if (!TryNormalizeHex(raw, out string hex, out string err))
            {
                DarkDialog.ShowWarning(err, "颜色格式不正确");
                AccentHexBox.Focus();
                AccentHexBox.SelectAll();
                return;
            }

            ApplyCustomAccent(hex);
        }

        /// <summary>应用自定义强调色：保存 Hex + 关闭跟随 + 立即生效 + 刷新界面状态。</summary>
        private void ApplyCustomAccent(string hex)
        {
            string norm;
            if (!TryNormalizeHex(hex, out norm, out _))
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
        /// 把用户输入规范成 #RRGGBB（大写）。
        /// 接受 #RGB / #RRGGBB / #AARRGGBB / RRGGBB；8 位时丢掉 Alpha 通道。
        /// 只做格式校验，不限制色相 —— 这是"自定义"该有的自由度。
        /// </summary>
        private static bool TryNormalizeHex(string input, out string hex, out string error)
        {
            hex = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(input))
            {
                error = "颜色值为空，需要 #RRGGBB 或 #RGB。";
                return false;
            }

            string v = input.Trim().TrimStart('#');

            // #AARRGGBB → 丢掉 Alpha
            if (v.Length == 8)
                v = v.Substring(2);

            // #RGB 缩写补全成 #RRGGBB
            if (v.Length == 3)
                v = string.Concat(v[0], v[0], v[1], v[1], v[2], v[2]);

            if (v.Length != 6)
            {
                error = "颜色值需要 6 位十六进制（如 #0078D4），或 3 位缩写（如 #08D）。";
                return false;
            }

            if (!v.All(Uri.IsHexDigit))
            {
                error = "颜色值只能包含 0-9 和 A-F。";
                return false;
            }

            hex = "#" + v.ToUpperInvariant();
            return true;
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            // 跟随系统强调色时，系统色可能被外部改掉（Windows 设置里换主题色）。
            // ThemeService 已把新色刷进画刷，这里只同步色板选中环与输入框。
            if (!_initialized || _suppress || !UiSettingsStore.Settings.FollowSystemAccent)
                return;

            SyncAccentUi();
        }

        // ==================== 启动与后台开关 ====================

        /// <summary>
        /// 开机静默自启：创建 / 删除计划任务 ZenForgeAutoStart。
        /// schtasks 调用需要几秒，放到后台线程，避免界面卡死。
        /// 失败时把开关拨回原位 —— 不能出现「界面显示开着、实际没注册」的假象。
        /// </summary>
        private async void AutoStartToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppress)
                return;

            bool enable = AutoStartToggle.IsChecked == true;

            AutoStartToggle.IsEnabled = false;
            try
            {
                bool ok = await Task.Run(() => enable ? AutoStartService.Enable() : AutoStartService.Disable());

                if (ok)
                {
                    UiSettingsStore.Settings.AutoStart = enable;
                    UiSettingsStore.Save();
                    LogService.Info(enable ? "开机静默自启：已注册计划任务" : "开机静默自启：已移除计划任务");
                }
                else
                {
                    SetToggle(AutoStartToggle, !enable);

                    DarkDialog.ShowWarning(
                        enable
                            ? "创建计划任务失败。请确认已以管理员身份运行，然后重试。\n\n详细信息见日志页。"
                            : "删除计划任务失败。请确认已以管理员身份运行，然后重试。\n\n详细信息见日志页。",
                        "ZenForge · 开机自启");
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"切换开机自启失败：{ex.Message}");
                SetToggle(AutoStartToggle, !enable);
                DarkDialog.ShowError($"操作失败：{ex.Message}", "ZenForge · 开机自启");
            }
            finally
            {
                AutoStartToggle.IsEnabled = true;
            }
        }

        /// <summary>启动时自动应用调校：仅影响 -silent 模式是否回写 SMU。</summary>
        private void ApplyTuningToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppress)
                return;

            bool enabled = ApplyTuningToggle.IsChecked == true;
            UiSettingsStore.Settings.ApplyTuningOnStartup = enabled;
            UiSettingsStore.Save();

            LogService.Info(enabled
                ? "自动应用调校：已开启（启动时将回写 tuning.json）"
                : "自动应用调校：已关闭（启动时不再回写 SMU）");
        }

        // ==================== 后台服务（总闸）。 ====================

        /// <summary>
        /// 开启后台服务：决定「点 × 之后进程还活不活」。
        ///
        /// 关掉时立刻把下面几行 Collapsed —— 不是灰显，是真的不显示。
        /// 灰显会让人以为「这一项存在但暂时点不了」，而这里是真的没这个概念。
        ///
        /// 同时把调度监听的启停跟上：总闸关了却还留着一个后台监听在跑，
        /// 就是「我明明关掉了它怎么还在占内存」的来源。
        /// </summary>
        private void BackendServiceToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppress)
                return;

            bool enabled = BackendServiceToggle.IsChecked == true;
            UiSettingsStore.Settings.BackendService = enabled;

            if (enabled)
                ReopenBackend();
            else
                CloseBackend();

            UiSettingsStore.Save();
            RefreshRowVisibility();

            LogService.Info(enabled
                ? "后台服务：已开启（点 × 不再退出进程）"
                : "后台服务：已关闭（点 × 将直接退出进程，调度与托盘一并停止）");
        }

        /// <summary>总闸关 = 不要后台。托盘和调度一起收，别留下看不见的活口。</summary>
        private void CloseBackend()
        {
            StopScheduling("关闭后台服务");
            TrayIconService.SetVisible(false);
            SetBackendDependentToggles(watch: false, tray: false);
        }

        /// <summary>重新打开总闸：把用户上次的意愿（ui.json 里的值）恢复成实际状态。</summary>
        private void ReopenBackend()
        {
            UiSettings ui = UiSettingsStore.Settings;

            if (ui.WatchProcessBinding)
                StartScheduling();

            SetBackendDependentToggles(watch: ProcessWatcher.IsRunning, tray: ui.ShowTrayIcon);

            if (ui.ShowTrayIcon)
                TrayIconService.EnsureCreated();
        }

        /// <summary>改这两个开关会各自触发 Click，先屏蔽掉再赋值。</summary>
        private void SetBackendDependentToggles(bool watch, bool tray) => Suppressed(() =>
        {
            ProcessWatchToggle.IsChecked = watch;
            ShowTrayToggle.IsChecked = tray;
        });

        /// <summary>仅留服务进程：只决定「关掉前台之后要不要销毁界面」，不决定后台存不存在。</summary>
        private void LightModeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppress)
                return;

            bool enabled = LightModeToggle.IsChecked == true;
            UiSettingsStore.Settings.LightMode = enabled;
            UiSettingsStore.Save();

            RefreshRowVisibility();

            LogService.Info(enabled
                ? $"仅留服务进程：已开启（关闭前台 {UiSettingsStore.Settings.LightModeDelaySeconds} 秒后销毁界面）"
                : "仅留服务进程：已关闭（关闭前台后仅隐藏窗口，界面完整保留）");
        }

        /// <summary>
        /// 依赖行的显隐，全页只有这一个开关函数。
        ///
        /// 「开启后台服务」是总闸：关掉后其余三项全部 Collapsed，
        /// 不存在「关了一半」的中间态 —— 用户不需要后台，这些设置也就无从谈起。
        /// 显隐一律 Visibility.Collapsed，禁止 IsEnabled=false 灰显。
        /// </summary>
        private void RefreshRowVisibility()
        {
            bool backend = BackendServiceToggle.IsChecked == true;
            Visibility v = backend ? Visibility.Visible : Visibility.Collapsed;

            RowProcessWatch.Visibility = v;
            RowLightMode.Visibility = v;
            RowShowTray.Visibility = v;
        }

        /// <summary>起调度监听，并把结果同步回开关与配置（Start 可能因全局互斥体静默失败）。</summary>
        private void StartScheduling()
        {
            try
            {
                ProcessWatcher.Start();
            }
            catch (Exception ex)
            {
                LogService.Error($"启动后台常驻调度失败：{ex.Message}");
            }
        }

        private static void StopScheduling(string reason)
        {
            try
            {
                ProcessWatcher.Stop();
                LogService.Info($"后台常驻调度已停止（{reason}）");
            }
            catch (Exception ex)
            {
                LogService.Error($"停止后台常驻调度失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 显示托盘图标。
        ///
        /// 只影响图标本身。关掉之后进程仍按「开启后台服务」决定留不留，
        /// 留的时候没有托盘入口 —— 靠双击 ZenForge.exe 唤回界面。
        /// </summary>
        private void ShowTrayToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppress)
                return;

            bool visible = ShowTrayToggle.IsChecked == true;

            UiSettingsStore.Settings.ShowTrayIcon = visible;
            UiSettingsStore.Save();

            TrayIconService.SetVisible(visible);

            LogService.Info(visible
                ? "托盘图标：已显示"
                : "托盘图标：已隐藏（后台服务仍在运行；双击 ZenForge.exe 唤出界面）");
        }

        /// <summary>
        /// 出错时通知我：只控制「要不要弹气泡」，不影响错误是否写进日志。
        ///
        /// 这个开关不做任何跨进程联动 —— 弹窗的决定在出错现场读取
        /// <see cref="UiSettings.NotifyOnError"/>，这里只负责改值 + 存盘。
        /// 之所以不在开关里直接调用任何通知服务：开关是「意愿」，
        /// 下一次出错才是「执行」，两者的调用点不该缠在一起。
        /// </summary>
        private void ErrorNotifyToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppress)
                return;

            bool enabled = ErrorNotifyToggle.IsChecked == true;

            UiSettingsStore.Settings.NotifyOnError = enabled;
            UiSettingsStore.Save();

            LogService.Info(enabled ? "错误通知：已开启" : "错误通知：已关闭（错误仍会写入日志）");
        }

        /// <summary>后台常驻调度：立即启停 ProcessWatcher。</summary>
        private void ProcessWatchToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppress)
                return;

            bool enabled = ProcessWatchToggle.IsChecked == true;

            try
            {
                if (enabled)
                    ProcessWatcher.Start();
                else
                    ProcessWatcher.Stop();

                // 以实际结果为准回写，别让开关停在「我以为开了」的状态。
                // Start() 拿不到全局互斥体会静默跳过，此时必须把开关拨回去并说清楚。
                bool actual = ProcessWatcher.IsRunning;
                SetToggle(ProcessWatchToggle, actual);

                UiSettingsStore.Settings.WatchProcessBinding = actual;
                UiSettingsStore.Save();

                // 托盘菜单里也有一个暂停/恢复，同步它的文案
                TrayIconService.RefreshMenuState();

                if (actual)
                {
                    LogService.Info(ProcessWatcher.UsingEvents
                        ? "后台常驻调度：已开启（WMI 事件驱动，空闲零轮询）"
                        : "后台常驻调度：已开启（WMI 不可用，已降级为定时轮询）");
                }
                else
                {
                    LogService.Info("后台常驻调度：已关闭");
                }

                if (enabled && !actual)
                {
                    DarkDialog.ShowWarning(
                        "后台调度监听未能启动。\n\n" +
                        "常见原因是已有另一个 ZenForge 实例在监听，同一台机器只允许一个。\n" +
                        "详细信息见日志页。",
                        "后台常驻调度");
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"切换后台常驻调度失败：{ex.Message}");
                SetToggle(ProcessWatchToggle, !enabled);
            }
        }

        // ==================== 其他：SMU 自检 ====================

        /// <summary>
        /// 跑一遍只读 + 回滚式自检，把结果写成 txt 并打开所在目录。
        ///
        /// 为什么需要它：CO 被整片拒绝 / FMax 回读为 0 这类问题，
        /// 光看应用日志分不清是「拓扑拆分不对」「SMU 不支持该消息」还是「权限不足」。
        /// 自检会把这些逐项打出来，用户贴一份文件就够了。
        ///
        /// 不做任何持久化改动：写入测试只写当前值（等于没改），并且写入后立即回读比对。
        /// </summary>
        private async void SmuDiagButton_Click(object sender, RoutedEventArgs e)
        {
            SmuDiagButton.IsEnabled = false;
            SmuDiagButton.Content = "正在自检…";

            string? path = null;
            try
            {
                path = await Task.Run(SmuDiagnostics.Run);
            }
            catch (Exception ex)
            {
                LogService.Error($"SMU 自检失败：{ex}");
                DarkDialog.ShowError($"自检过程中出错：\n{ex.Message}", "自检失败");
                return;
            }
            finally
            {
                SmuDiagButton.Content = "运行";
                SmuDiagButton.IsEnabled = true;
            }

            if (string.IsNullOrEmpty(path))
            {
                DarkDialog.ShowWarning("自检未能生成报告文件。", "自检");
                return;
            }

            LogService.Info($"SMU 自检完成，报告：{path}");
            RevealInExplorer(path);
            DarkDialog.ShowSuccess($"自检完成，报告已生成：\n{path}", "SMU 自检");
        }

        /// <summary>打开所在目录并选中文件，省去用户自己找。</summary>
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
                // 打不开资源管理器不影响报告已生成
            }
        }

        // ==================== 其他：全局重置 ====================

        /// <summary>
        /// 全局重置：先确认，再恢复出厂配置。
        /// SMU 可用时把 CO（全 0）/ FMax（基线或 5400）写回 SMU；
        /// 不可用时仅重置本地配置，提示下次启动按出厂值应用。
        /// </summary>
        private async void GlobalResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (!DarkDialog.Confirm(
                    "确定要将所有调校恢复出厂设置吗？\n此操作会清除已保存的 CO / FMax 设定。",
                    "全局重置"))
                return;

            uint effectiveFMax = TuningStore.ResetToFactory();

            SmuReader reader = SmuReader.Shared;
            if (!reader.IsAvailable)
            {
                DarkDialog.Show("已重置本地配置，下次启动时将按出厂值应用。", "全局重置");
                LogService.Info("全局重置：SMU 不可用，仅重置本地配置");
                return;
            }

            // 写入期间禁用按钮：这条路径不经过调校页的 _busy 闸门，
            // 点了没反应总比连点两次并发下发好（SmuReader 内部虽然串行化，
            // 但两次重置会各写一遍、日志也重复）。
            GlobalResetButton.IsEnabled = false;
            try
            {
                await WriteFactoryToSmuAsync(reader, effectiveFMax);
            }
            finally
            {
                GlobalResetButton.IsEnabled = true;
            }
        }

        private static async Task WriteFactoryToSmuAsync(SmuReader reader, uint effectiveFMax)
        {
            // 核数拿不到就不要猜。以前回落 16，在非 16 核机器上会
            // 要么漏核（写入不完整）、要么对不存在的核下发 0。
            int coreCount = reader.CoreCount;
            if (coreCount <= 0)
            {
                DarkDialog.Show(
                    "已重置本地配置，但读不到 CPU 核心数，未写回 SMU。\n" +
                    "请先到「调校」页刷新一次再重试。",
                    "全局重置");
                LogService.Error("全局重置：CoreCount 未知，跳过 SMU 写回");
                return;
            }

            try
            {
                SmuWriteResult co = await Task.Run(() => reader.WriteCurveOptimizer(new int[coreCount]));

                // FMax 基线未知（从未探测到）时只归零 CO，不拿猜来的值写 FMax
                SmuFMaxResult fm = effectiveFMax > 0
                    ? await Task.Run(() => reader.WriteFMax(effectiveFMax))
                    : new SmuFMaxResult { AllSucceeded = true, ReadBack = 0 };

                if (!co.AllSucceeded || !fm.AllSucceeded)
                {
                    LogService.Error(co.AllSucceeded
                        ? $"全局重置：FMax 写回 SMU 失败（回读 {fm.ReadBack}）"
                        : "全局重置：CO 写回 SMU 失败");
                    DarkDialog.Show("已重置本地配置，但写回 SMU 部分失败，详见日志。", "全局重置");
                    return;
                }

                string fmaxLine = effectiveFMax > 0
                    ? $"FMax：{effectiveFMax} MHz（回读 {fm.ReadBack}）"
                    : "FMax：基线未知，未写回";

                DarkDialog.Show(
                    $"已恢复出厂设置并写回 SMU。\n\nCO：{coreCount} 核归零\n{fmaxLine}",
                    "全局重置");

                LogService.Info("全局重置：CO 归零" +
                    (effectiveFMax > 0 ? $" + FMax {effectiveFMax} MHz 已写回 SMU" : "（FMax 未写回）"));
            }
            catch (Exception ex)
            {
                LogService.Error($"全局重置写回 SMU 失败: {ex.Message}");
                DarkDialog.Show("已重置本地配置，但写回 SMU 失败，详见日志。", "全局重置");
            }
        }
    }
}
