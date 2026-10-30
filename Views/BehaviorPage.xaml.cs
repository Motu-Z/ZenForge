using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZenForge.Helpers;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// 行为页：启动 / 后台 / 通知。
    ///
    /// 【开关的写入纪律】
    /// 所有开关都是「先改设置 → 存盘 → 记录」，绝不在这里直接调用会改硬件的东西。
    /// 唯一例外是总闸：关掉总闸必须立刻停掉监听与托盘，否则就是
    /// 「我明明关了后台，它怎么还在占内存」。
    ///
    /// 【初始化顺序：先赋值、后挂事件】
    /// 反向会出事：给 ComboBox / ToggleButton 赋值同样会触发 Changed / Click，
    /// 于是「打开页面」这个动作自己就把用户的设置改回默认值了。
    /// </summary>
    public partial class BehaviorPage : Page, IPageLifecycle
    {
        public const string PageKey = "behavior";

        /// <summary>屏蔽开关回调：程序化赋值不算用户操作。</summary>
        private bool _suppress;

        /// <summary>开机自启的真实状态只在第一次进入本页时校准一次（schtasks 查询较慢）。</summary>
        private bool _autoStartCalibrated;

        public BehaviorPage()
        {
            InitializeComponent();

            // ↓↓↓ 顺序不可调换：先按 ui.json 回写控件，再挂点击回调 ↓↓↓
            LoadToggles();
        }

        public void OnActivated()
        {
            if (!_autoStartCalibrated)
                CalibrateAutoStart();
        }

        public void OnDeactivated()
        {
        }

        private void LoadToggles() => Suppressed(() =>
        {
            UiSettings s = UiSettingsStore.Settings;

            AutoStartToggle.IsChecked = s.AutoStart;
            ApplyTuningToggle.IsChecked = s.ApplyTuningOnStartup;
            BackendServiceToggle.IsChecked = s.BackendService;
            ProcessWatchToggle.IsChecked = s.WatchProcessBinding && ProcessWatcher.IsRunning;
            LightModeToggle.IsChecked = s.LightMode;
            ShowTrayToggle.IsChecked = s.ShowTrayIcon;
            ErrorNotifyToggle.IsChecked = s.NotifyOnError;

            RefreshSubOptions();
        });

        /// <summary>
        /// 异步校准「开机静默自启」的真实状态。
        ///
        /// 为什么不能只信 ui.json：用户可能在「任务计划程序」里手动删掉了
        /// ZenForgeAutoStart，或换机器后配置被拷贝过来。必须以计划任务的
        /// 实际存在性为准，否则界面会谎报「已开启」。
        /// </summary>
        private async void CalibrateAutoStart()
        {
            _autoStartCalibrated = true;

            try
            {
                bool taskExists = await Task.Run(AutoStartService.IsEnabled);

                // 校准期间用户可能已经手动拨过开关，此时不要再覆盖他的操作
                if (_suppress)
                    return;

                Suppressed(() => AutoStartToggle.IsChecked = taskExists);

                if (taskExists == UiSettingsStore.Settings.AutoStart)
                    return;

                UiSettingsStore.Settings.AutoStart = taskExists;
                UiSettingsStore.Save();

                LogService.Info(taskExists
                    ? "已校准开机自启状态：计划任务存在，开关显示为开启"
                    : "已校准开机自启状态：计划任务不存在，开关已复位为关闭");
            }
            catch (Exception ex)
            {
                LogService.Warn($"校准开机自启状态失败：{ex.Message}");
            }
        }

        // ==================== 启动 ====================

        /// <summary>
        /// 开机静默自启。
        ///
        /// 注册 / 注销计划任务可能失败（权限不足），失败时必须把开关拨回去：
        /// 不能出现「界面显示开着、实际没注册」的假象。
        /// </summary>
        private async void AutoStartToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppress)
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
                    ToastService.Error(enable
                        ? "创建计划任务失败，请确认已以管理员身份运行后重试（详细信息见日志页）"
                        : "删除计划任务失败，请确认已以管理员身份运行后重试（详细信息见日志页）");
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"切换开机自启失败：{ex.Message}");
                SetToggle(AutoStartToggle, !enable);
                ToastService.Error($"开机自启切换失败：{ex.Message}");
            }
            finally
            {
                AutoStartToggle.IsEnabled = true;
            }
        }

        /// <summary>启动时自动应用调校：只影响 -silent 模式是否回写 SMU。</summary>
        private void ApplyTuningToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppress)
                return;

            bool enabled = ApplyTuningToggle.IsChecked == true;
            UiSettingsStore.Settings.ApplyTuningOnStartup = enabled;
            UiSettingsStore.Save();

            LogService.Info(enabled
                ? "自动应用调校：已开启（启动时将回写 tuning.json）"
                : "自动应用调校：已关闭（启动时不再回写 SMU）");
        }

        // ==================== 后台（总闸） ====================

        /// <summary>
        /// 开启后台服务：决定「点 × 之后进程还活不活」。
        ///
        /// 关掉时把子选项整块 Collapsed —— 不是灰显，是真的不显示。
        /// 灰显会让人以为「这一项存在但暂时点不了」，而这里是真的没这个概念。
        /// 同时把调度监听与托盘的启停跟上：总闸关了却还留着一个后台监听在跑，
        /// 就是「我明明关掉了它怎么还在占内存」的来源。
        /// </summary>
        private void BackendServiceToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppress)
                return;

            bool enabled = BackendServiceToggle.IsChecked == true;
            UiSettingsStore.Settings.BackendService = enabled;

            if (enabled)
                ReopenBackend();
            else
                CloseBackend();

            UiSettingsStore.Save();
            RefreshSubOptions();

            LogService.Info(enabled
                ? "后台服务：已开启（点 × 不再退出进程）"
                : "后台服务：已关闭（点 × 将直接退出进程，调度与托盘一并停止）");
        }

        /// <summary>总闸关 = 不要后台。托盘和调度一起收，别留下看不见的活口。</summary>
        private void CloseBackend()
        {
            StopScheduling("关闭后台服务");
            TrayIconService.SetVisible(false);

            Suppressed(() =>
            {
                ProcessWatchToggle.IsChecked = false;
                ShowTrayToggle.IsChecked = false;
            });
        }

        /// <summary>重新打开总闸：把用户上次的意愿（ui.json 里的值）恢复成实际状态。</summary>
        private void ReopenBackend()
        {
            UiSettings ui = UiSettingsStore.Settings;

            if (ui.WatchProcessBinding)
                StartScheduling();

            Suppressed(() =>
            {
                ProcessWatchToggle.IsChecked = ProcessWatcher.IsRunning;
                ShowTrayToggle.IsChecked = ui.ShowTrayIcon;
            });

            if (ui.ShowTrayIcon)
                TrayIconService.EnsureCreated();
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

        /// <summary>仅留服务进程：只决定「关掉前台之后要不要销毁界面」，不决定后台存不存在。</summary>
        private void LightModeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppress)
                return;

            bool enabled = LightModeToggle.IsChecked == true;
            UiSettingsStore.Settings.LightMode = enabled;
            UiSettingsStore.Save();

            LogService.Info(enabled
                ? $"后台轻量模式：已开启（关闭前台 {UiSettingsStore.Settings.LightModeDelaySeconds} 秒后销毁界面）"
                : "后台轻量模式：已关闭（关闭前台后仅隐藏窗口，界面完整保留）");
        }

        /// <summary>
        /// 后台常驻调度：立即启停 ProcessWatcher。
        ///
        /// 以实际结果为准回写：Start() 拿不到全局互斥体会静默跳过，
        /// 此时必须把开关拨回去 —— 停在「我以为开了」的状态最糟糕。
        /// </summary>
        private void ProcessWatchToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppress)
                return;

            bool enabled = ProcessWatchToggle.IsChecked == true;

            try
            {
                if (enabled)
                    ProcessWatcher.Start();
                else
                    ProcessWatcher.Stop();

                bool actual = ProcessWatcher.IsRunning;
                SetToggle(ProcessWatchToggle, actual);

                UiSettingsStore.Settings.WatchProcessBinding = actual;
                UiSettingsStore.Save();

                TrayIconService.RefreshMenuState();   // 托盘菜单里也有一个暂停 / 恢复

                if (actual)
                {
                    LogService.Info(ProcessWatcher.UsingEvents
                        ? "后台常驻调度：已开启（WMI 事件驱动，空闲零轮询）"
                        : "后台常驻调度：已开启");
                }
                else
                {
                    LogService.Info("后台常驻调度：已关闭");
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"切换后台常驻调度失败：{ex.Message}");
                SetToggle(ProcessWatchToggle, !enabled);
            }
        }

        /// <summary>
        /// 显示托盘图标。只影响图标本身：关掉之后进程仍由「开启后台服务」决定留不留，
        /// 留着的时候没有托盘入口 —— 靠双击 ZenForge.exe 唤回界面。
        /// </summary>
        private void ShowTrayToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppress)
                return;

            bool visible = ShowTrayToggle.IsChecked == true;

            UiSettingsStore.Settings.ShowTrayIcon = visible;
            UiSettingsStore.Save();

            TrayIconService.SetVisible(visible);

            LogService.Info(visible
                ? "托盘图标：已显示"
                : "托盘图标：已隐藏（后台服务仍在运行；双击 ZenForge.exe 唤出界面）");
        }

        // ==================== 通知 ====================

        /// <summary>
        /// 出错时通知我：只控制「要不要弹气泡」，不影响错误是否写进日志。
        ///
        /// 这个开关不做任何跨进程联动 —— 弹窗的决定在出错现场读取
        /// <see cref="UiSettings.NotifyOnError"/>，这里只负责改值 + 存盘。
        /// 开关是「意愿」，下一次出错才是「执行」，两者的调用点不该缠在一起。
        /// </summary>
        private void ErrorNotifyToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppress)
                return;

            bool enabled = ErrorNotifyToggle.IsChecked == true;
            UiSettingsStore.Settings.NotifyOnError = enabled;
            UiSettingsStore.Save();

            LogService.Info(enabled ? "错误通知：已开启" : "错误通知：已关闭（错误仍会写入日志）");
        }

        // ==================== 共用 ====================

        private void RefreshSubOptions()
            => SubOptions.Visibility = BackendServiceToggle.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        /// <summary>在屏蔽回调的前提下改控件状态，避免初始化 / 回滚被当成用户操作。</summary>
        private void Suppressed(Action action)
        {
            _suppress = true;
            try { action(); }
            finally { _suppress = false; }
        }

        private void SetToggle(ToggleButton toggle, bool value) => Suppressed(() => toggle.IsChecked = value);
    }
}
