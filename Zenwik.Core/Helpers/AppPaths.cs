using System;
using System.Diagnostics;
using System.IO;

namespace Zenwik.Core.Helpers
{
    /// <summary>
    /// 产品级路径的唯一出口。
    ///
    /// 【为什么要有这个类】
    /// 改名前，`%AppData%\ZenForge` 这个字面量在 9 个文件里各写了一遍
    /// （4 个 Store + 日志 + 诊断 + 工厂重置 + 方案 + 场景）。
    /// 改名时只要漏一处，用户就会得到「设置能存、方案读不出来」这种
    /// 一半新一半旧的错乱状态 —— 而且不会有任何报错。
    /// 现在只有这里知道目录名，别处一律问它要。
    ///
    /// 【一次性迁移】
    /// 旧目录 `%AppData%\ZenForge` 若存在、新目录 `%AppData%\Zenwik` 不存在，
    /// 首次访问时整目录搬过去。用户升级后不该丢掉自己的方案、绑核规则、调校值。
    /// 迁移失败（占用 / 权限 / 跨卷）不抛异常：退化为「用新目录、旧数据留着不动」，
    /// 用户的原始数据至少不会因为一次改名而消失。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>产品名。凡是需要「显示或落盘用哪个名字」的地方，一律引用这里。</summary>
        public const string ProductName = "Zenwik";

        /// <summary>改名前的目录名，仅用于一次性迁移。</summary>
        private const string LegacyProductName = "ZenForge";

        private static readonly object Gate = new();
        private static string? _root;

        /// <summary>`%AppData%\Zenwik`，首次访问时建目录并完成旧数据迁移。</summary>
        public static string Root
        {
            get
            {
                if (_root is { } cached)
                    return cached;

                lock (Gate)
                {
                    if (_root is { } again)
                        return again;

                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string target = Path.Combine(appData, ProductName);

                    MigrateLegacy(Path.Combine(appData, LegacyProductName), target);

                    try
                    {
                        Directory.CreateDirectory(target);
                    }
                    catch (Exception ex)
                    {
                        // 建不出来也得把路径返回去：调用方自己写文件时会拿到明确的
                        // UnauthorizedAccess / DirectoryNotFound，比在这里抛更接近现场。
                        Debug.WriteLine($"创建配置目录失败：{ex.Message}");
                    }

                    _root = target;
                    return target;
                }
            }
        }

        /// <summary>日志目录 `%AppData%\Zenwik\Logs`。</summary>
        public static string Logs => Path.Combine(Root, "Logs");

        /// <summary>配置目录下的某个文件，例如 <c>File("profiles.json")</c>。</summary>
        public static string File(string fileName) => Path.Combine(Root, fileName);

        /// <summary>配置目录下的某个子目录，例如 <c>Dir("Diagnostics")</c>。</summary>
        public static string Dir(string name) => Path.Combine(Root, name);

        /// <summary>
        /// 旧目录 → 新目录的一次性搬移。只在「新目录不存在」时动手，
        /// 因此对已经迁移过的用户是零开销（两次 <see cref="Directory.Exists"/>）。
        /// </summary>
        private static void MigrateLegacy(string legacy, string target)
        {
            try
            {
                if (!Directory.Exists(legacy) || Directory.Exists(target))
                    return;

                Directory.Move(legacy, target);
                Debug.WriteLine($"配置目录已从 {legacy} 迁移到 {target}");
            }
            catch (Exception ex)
            {
                // 搬不动就复制（跨卷、被占用时 Move 会失败），复制也失败就放弃。
                try
                {
                    CopyTree(legacy, target);
                    Debug.WriteLine($"配置目录已从 {legacy} 复制到 {target}（Move 失败：{ex.Message}）");
                }
                catch (Exception copyEx)
                {
                    Debug.WriteLine($"配置目录迁移失败，保留旧目录：{copyEx.Message}");
                }
            }
        }

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, destination));

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                System.IO.File.Copy(file, file.Replace(source, destination), overwrite: false);
        }
    }
}
