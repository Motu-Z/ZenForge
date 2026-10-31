using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zentwik.Core.Serialization;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 界面设置（主题 / 强调色），持久化到 %APPDATA%\Zentwik\ui.json
    /// </summary>
    public sealed class UiSettings
    {
        /// <summary>出厂强调色。与 ThemeService 的默认色、通用页色板的兜底色同一值。</summary>
        public const string DefaultAccentHex = "#38BDF8";

        [JsonConverter(typeof(JsonStringEnumConverter<AppThemeMode>))]
        public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

        /// <summary>
        /// 强调色是否跟随 Windows 系统强调色。
        /// **默认 true**：首次安装、或配置文件里没有这个键时，一律跟随系统。
        /// </summary>
        public bool FollowSystemAccent { get; set; } = true;

        /// <summary>自定义强调色（十六进制，如 #38BDF8）。仅 FollowSystemAccent=false 时生效。</summary>
        public string CustomAccentHex { get; set; } = "#38BDF8";

        /// <summary>配置文件里是否显式写过 followSystemAccent（旧配置迁移用，不参与序列化）。</summary>
        [JsonIgnore]
        public bool FollowSystemAccentExplicit { get; set; }

        // 启动与后台

        /// <summary>
        /// 开机静默自启：是否已注册 Windows 计划任务 ZentwikAutoStart。
        /// 真实状态以计划任务是否存在为准，本字段仅作缓存与界面回显。
        /// </summary>
        public bool AutoStart { get; set; }

        /// <summary>
        /// 启动时自动应用调校：静默模式（-silent）下是否把 tuning.json 的 CO / FMax 写回 SMU。
        /// </summary>
        public bool ApplyTuningOnStartup { get; set; } = true;

        /// <summary>
        /// 后台常驻调度：为 true 时进程常驻，按 bindings.json 的规则给新进程自动绑核。
        ///
        /// 注意它不是总闸 —— 要不要有后台由 <see cref="BackendService"/> 决定，
        /// 这一项只是「后台要不要干调度这件事」。
        /// </summary>
        public bool WatchProcessBinding { get; set; } = true;

        /// <summary>
        /// 【总闸】开启后台服务。关掉 = 用户根本不需要后台：点 X 直接彻底退出进程，
        /// 监听 / 托盘 / 单实例锁一并收摊，不搞任何隐藏。打开后才会露出
        /// 「后台常驻调度 / 仅留服务进程 / 显示托盘图标」。
        /// 默认 false：没人希望装完就被一个退不掉的常驻进程缠上。
        /// </summary>
        public bool BackendService { get; set; }

        /// <summary>
        /// 仅留服务进程：开启时点 X 先隐藏、倒计时结束后销毁前台界面，进程只剩后台服务；
        /// 关闭时点 X 只是隐藏窗口，界面完整保留，回来是瞬间的。
        ///
        /// 曾经并存的「仅清空页面」选项实测内存表现一致（工作集都压到个位数 MB），
        /// 留两个只增加理解成本，已删。
        /// </summary>
        public bool LightMode { get; set; }

        /// <summary>
        /// 轻量模式延迟（秒）：关闭前台后等多久销毁界面。
        ///
        /// 目前固定 3 秒、界面不暴露；保留字段是为了让调用方读值而不是写死字面量。
        /// 3 秒只是给「刚点 × 就想反悔」留个回头窗口，不是给系统留准备时间 ——
        /// 真等半分钟才销毁，用户早就忘了自己还开着它。
        /// </summary>
        public int LightModeDelaySeconds { get; set; } = 3;

        /// <summary>
        /// 显示托盘图标。关掉后进程仍常驻（调度照跑），但没有可见入口，只能双击 exe 回来。
        /// 默认 true：用户至少得能看见它在跑，也能就地退出。
        /// </summary>
        public bool ShowTrayIcon { get; set; } = true;

        /// <summary>
        /// 出错时用系统气泡提示（非阻塞）。
        ///
        /// 默认 true：失败的写入 / 驱动异常必须让用户看见。
        /// 但调 SMU 时偶发的一次读失败并不严重，用户如果觉得弹得烦可以关掉，
        /// 错误仍然完整写进日志页，不会被丢弃。
        /// </summary>
        public bool NotifyOnError { get; set; } = true;
    }

    public class UiSettingsStore : IUiSettingsStore
    {
        private readonly IAppPaths _paths;

        public UiSettingsStore(IAppPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _paths = paths;
        }

        private string FilePath => _paths.File("ui.json");

        public UiSettings Settings { get; private set; } = new();

        public void Load()
        {
            try
            {
                Settings = ReadFromDisk() ?? new UiSettings();
            }
            catch
            {
                Settings = new UiSettings();
            }

            Normalize();
        }

        private UiSettings? ReadFromDisk()
        {
            if (!File.Exists(FilePath))
                return null;

            string json = File.ReadAllText(FilePath);
            UiSettings settings = JsonSerializer.Deserialize(json, AppJsonContext.Default.UiSettings) ?? new UiSettings();

            // 旧版 ui.json 的 followSystemAccent 默认 false 且配过写死的青色，
            // 于是老用户打开程序看到的都是硬编码青。文件里没写过这个键 = 从未设置过，
            // 强制回到「跟随系统」；明确写过（无论 true/false）就尊重用户。
            using JsonDocument doc = JsonDocument.Parse(json);
            settings.FollowSystemAccentExplicit =
                doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(nameof(UiSettings.FollowSystemAccent), out _);

            if (!settings.FollowSystemAccentExplicit)
                settings.FollowSystemAccent = true;

            return settings;
        }

        /// <summary>
        /// 把数值类字段拉回合法区间：配置文件是纯文本，人手改坏或从旧版本继承
        /// 一个 0 / 负数都是常事，在这里统一收口，读出来的值就能直接用。
        /// </summary>
        private void Normalize()
        {
            // 延迟目前不在界面暴露，一律回到默认 3 秒（旧配置里可能是 1 或 30）。
            // 等输入框加回来，这里再改成只做区间收敛。
            Settings.LightModeDelaySeconds = 3;
        }

        /// <summary>
        /// 恢复出厂：整份设置换回默认实例并落盘。
        ///
        /// 刻意逐字段赋值而不是 new 一个再替换：
        ///   · 逐字段写漏一个，就是「重置后仍保留上一台机器的设置」；
        ///   · 整体替换更彻底，且将来新增字段自动被带上默认值。
        ///
        /// 顺便清掉 FollowSystemAccentExplicit：重置后不该再带着
        /// 「用户曾显式设过强调色」的历史标记，否则下次读盘时会跳过迁移逻辑。
        /// </summary>
        public void ResetToFactory()
        {
            Settings = new UiSettings();
            Settings.FollowSystemAccentExplicit = false;
            Save();
        }

        public void Save()
        {
            try
            {
                JsonFile.Write(FilePath, Settings, AppJsonContext.Default.UiSettings);
            }
            catch
            {
                // 设置保存失败不影响主流程
            }
        }
    }
}
