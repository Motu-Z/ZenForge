using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 配置目录解析结果。
    /// </summary>
    public interface IAppPaths
    {
        /// <summary>配置根目录，例如 <c>%AppData%\Zentwik</c>。首次访问时创建。</summary>
        string Root { get; }

        /// <summary>日志目录。</summary>
        string Logs { get; }

        /// <summary>配置根目录下的某个文件，例如 <c>File("tuning.json")</c>。</summary>
        string File(string fileName);

        /// <summary>配置根目录下的某个子目录，例如 <c>Dir("Diagnostics")</c>。</summary>
        string Dir(string name);

        /// <summary>本次启动是否搬迁了旧版本目录，供日志与自检使用。</summary>
        MigrationOutcome Migration { get; }
    }

    /// <summary>
    /// 旧版本目录搬迁的结果。没搬成也要能把原因写进日志，
    /// 否则用户只会看到「我的方案没了」。
    /// </summary>
    public readonly record struct MigrationOutcome(
        bool Attempted,
        bool Succeeded,
        string? SourceDirectory,
        string? Error)
    {
        public static MigrationOutcome NotNeeded { get; } = new(false, true, null, null);

        public static MigrationOutcome From(string source) => new(true, true, source, null);

        public static MigrationOutcome Failed(string source, string error) => new(true, false, source, error);
    }

    /// <summary>
    /// 产品级路径的唯一出口。
    ///
    /// 目录名的字面量只允许出现在本文件：改名时漏掉任何一处，用户都会得到
    /// 「设置能存、方案读不出来」这种一半新一半旧的状态，且不会报错。
    ///
    /// 迁移链：ZenForge → Zenwik → Zentwik。
    /// 新目录有内容就不搬（用户已经在用新版）；新目录不存在或为空才搬，
    /// 因此对已迁移过的用户零开销（两次目录探测）。
    /// 搬迁失败不抛异常：退化为「用新目录、旧数据原地不动」，宁可让用户手动找，
    /// 也不能因为一次改名把他的调校值删掉。
    /// </summary>
    public sealed class AppPaths : IAppPaths
    {
        public const string ProductName = "Zentwik";

        public const string LogsDirectoryName = "Logs";

        /// <summary>按新→旧排列。同名并存时只搬最新的那份。</summary>
        private static readonly string[] LegacyProductNames = ["Zenwik", "ZenForge"];

        private const string MigrationMutexName = @"Local\Zentwik.ConfigMigration";

        private static readonly TimeSpan MigrationLockTimeout = TimeSpan.FromSeconds(5);

        private readonly string _appDataRoot;

        private readonly Lazy<string> _root;

        private MigrationOutcome _migration = MigrationOutcome.NotNeeded;

        public AppPaths()
            : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        {
        }

        /// <param name="appDataRoot">%AppData% 根目录。测试时指向临时目录即可隔离真实配置。</param>
        public AppPaths(string appDataRoot)
        {
            ArgumentException.ThrowIfNullOrEmpty(appDataRoot);

            _appDataRoot = appDataRoot;
            _root = new Lazy<string>(ResolveRoot, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public string Root => _root.Value;

        public string Logs => Path.Combine(Root, LogsDirectoryName);

        public MigrationOutcome Migration => _migration;

        public string File(string fileName)
        {
            ArgumentException.ThrowIfNullOrEmpty(fileName);
            return Path.Combine(Root, fileName);
        }

        public string Dir(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            return Path.Combine(Root, name);
        }

        private string ResolveRoot()
        {
            string target = Path.Combine(_appDataRoot, ProductName);

            if (HasContent(target))
                return target;

            // 迁移必须互斥：开机自启与用户手动启动可能同时发生在两个进程里。
            // 两边同时 Directory.Move 会让其中一个以「目录已存在」失败，
            // 之后各自按不同的结果继续跑，配置就分裂成两份。
            Mutex? gate = TryAcquireMigrationGate(out bool owned);

            try
            {
                // 拿到锁后重新判断：先到的进程可能已经搬完了。
                if (HasContent(target))
                    return target;

                MigrateFromLegacy(target);
            }
            finally
            {
                if (gate is not null && owned)
                {
                    try
                    {
                        gate.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // 未被当前线程持有。退出路径上不值得再抛。
                    }
                }

                gate?.Dispose();
            }

            EnsureDirectory(target);

            return target;
        }

        private void MigrateFromLegacy(string target)
        {
            foreach (string legacyName in LegacyProductNames)
            {
                string legacy = Path.Combine(_appDataRoot, legacyName);

                if (!Directory.Exists(legacy))
                    continue;

                try
                {
                    if (!Directory.Exists(target))
                    {
                        Directory.Move(legacy, target);
                        _migration = MigrationOutcome.From(legacyName);
                        return;
                    }

                    // 目标目录已被上一次失败的启动建出来，但里面是空的：
                    // 仍然把旧数据搬进去，而不是让用户面对一个空的新目录。
                    CopyTree(legacy, target);
                    _migration = MigrationOutcome.From(legacyName);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Move 失败多半是跨卷或被占用，退回逐文件复制。
                    try
                    {
                        CopyTree(legacy, target);
                        _migration = MigrationOutcome.From(legacyName);
                    }
                    catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
                    {
                        _migration = MigrationOutcome.Failed(legacyName, copyEx.Message);
                    }

                    return;
                }
            }
        }

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, directory);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string target = Path.Combine(destination, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                System.IO.File.Copy(file, target, overwrite: true);
            }
        }

        private static bool HasContent(string directory)
        {
            try
            {
                return Directory.Exists(directory)
                    && Directory.EnumerateFileSystemEntries(directory).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 探测不了就当作有内容：宁可跳过迁移，也不要覆盖用户正在用的目录。
                return true;
            }
        }

        private static Mutex? TryAcquireMigrationGate(out bool owned)
        {
            owned = false;

            try
            {
                Mutex mutex = new(initiallyOwned: false, MigrationMutexName);

                try
                {
                    owned = mutex.WaitOne(MigrationLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个持锁进程崩溃退出了。锁已经归我们，继续迁移。
                    owned = true;
                }

                if (owned)
                    return mutex;

                mutex.Dispose();
                return null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                // 命名互斥体创建不出来（受限会话）。单进程场景没有并发迁移风险；
                // 多进程时最坏结果是两边都复制一遍 —— 内容相同，不会损坏数据。
                Debug.WriteLine($"配置迁移互斥体不可用：{ex.Message}");
                return null;
            }
        }

        private static void EnsureDirectory(string target)
        {
            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 建不出来仍然返回路径：调用方写文件时会拿到更贴近现场的异常，
                // 在这里抛只会让程序在启动阶段就死掉。
                Debug.WriteLine($"创建配置目录失败：{ex.Message}");
            }
        }
    }
}
