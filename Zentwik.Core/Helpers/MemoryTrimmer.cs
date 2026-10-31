using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Zentwik.Core.Services;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 轻量模式的内存回收。
    ///
    /// 为什么必须有这一步：WPF 的对象死了不等于内存还给了系统。
    ///   · 托管堆：GC 收掉对象后，已提交的 segment 仍留在进程里，默认不还给 OS；
    ///   · 大对象堆（LOH）：页面树、位图、字符串数组多半落在这里，不压缩就一直是空洞；
    ///   · 工作集：任务管理器看的「内存」是 working set，只能靠 SetProcessWorkingSetSize 主动换出。
    ///
    /// 只做 GC 不做最后一步，两条轻量路径在任务管理器里看起来会一模一样 —— 这正是
    /// 「仅清空页面」和「仅驻留服务」测得差不多数字的原因：两边都只丢了引用，没交还内存。
    /// </summary>
    internal class MemoryTrimmer
    {
        private readonly ILogService _log;

        public MemoryTrimmer(ILogService log)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>此刻的工作集与私有提交（字节）。取不到时返回 0。</summary>
        private (long WorkingSet, long Private) Snapshot()
        {
            try
            {
                using Process p = Process.GetCurrentProcess();
                p.Refresh();
                return (p.WorkingSet64, p.PrivateMemorySize64);
            }
            catch
            {
                return (0, 0);
            }
        }

        /// <summary>
        /// 强制回收 + 压缩 LOH + 交还工作集，并把前后数字写进日志。
        ///
        /// 会阻塞几十到几百毫秒，**不要在 UI 线程上直接调** ——
        /// 现有的两个调用点都在 Task 里，或者排在 ApplicationIdle 之后。
        /// </summary>
        internal void Trim(string reason)
        {
            (long wsBefore, long privBefore) = Snapshot();

            try
            {
                // LOH 压缩必须成对出现：先举手，再带 compacting: true 收一次，
                // 否则这里只是把大对象标记成可回收，空洞仍在。
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

                // 收两轮：第一轮把对象变垃圾（终结器队列），
                // WaitForPendingFinalizers 让终结器跑完，第二轮才真正收干净。
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch (Exception ex)
            {
                _log.Warn($"内存回收（{reason}）GC 阶段失败：{ex.Message}");
            }

            try
            {
                // -1 / -1 等价 EmptyWorkingSet：把工作集换出到页面文件。
                // 这是任务管理器数字真正掉下来的唯一办法；代价是下次回来要重新缺页，
                // 而轻量模式本来赌的就是「用户很久不回来」。
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch (Exception ex)
            {
                _log.Warn($"内存回收（{reason}）交还工作集失败：{ex.Message}");
            }

            (long wsAfter, long privAfter) = Snapshot();
            _log.Info(
                $"内存回收（{reason}）：工作集 {Mb(wsBefore)} → {Mb(wsAfter)}，私有提交 {Mb(privBefore)} → {Mb(privAfter)}");
        }

        /// <summary>后台跑一次 Trim，调用方不等结果。</summary>
        internal void TrimInBackground(string reason)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Trim(reason);
                }
                catch (Exception ex)
                {
                    _log.Warn($"内存回收（{reason}）失败：{ex.Message}");
                }
            });
        }

        private static string Mb(long bytes) => bytes <= 0 ? "?" : $"{bytes / 1024.0 / 1024.0:F1} MB";
    }
}
