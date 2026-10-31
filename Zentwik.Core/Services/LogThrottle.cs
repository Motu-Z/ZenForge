using System;
using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 日志节流：同一个 key 在一个时间窗内只放行一次，其余计数后补报。
    ///
    /// 【为什么需要它】
    /// 绑核是对着「系统里所有进程」做的操作，而总有一批进程是打不开的：
    /// 受保护进程（PPL，错误 5）、带反作弊的、刚好退出的。这些失败是**预期内**的，
    /// 但每来一个新实例就写一行 WARN —— MicrosoftEdgeUpdate 这类常驻更新器
    /// 一天能拉起几十次，日志页两分钟就被「OpenProcess 失败（错误 5）」刷满，
    /// 真正有用的那几条（谁绑成功了、谁因为拓扑不对失败）被埋掉。
    ///
    /// 【为什么不直接把这类错误降成 Debug】
    /// 那等于把「绑不上」这件事藏起来。用户配了规则却没生效，第一反应是查日志，
    /// 而日志里什么都没有，比刷屏更糟。这里选的是折中：**留一条，但要说明被压了多少**。
    ///
    /// 【计数口径】
    /// suppressed 统计的是「上一个窗口内被压掉的条数」，由下一次放行时带出去。
    /// 也就是说 N 总是滞后的 —— 但日志本来就是事后看的，滞后一窗没有实际影响，
    /// 换来的是不需要额外的定时器去冲刷（后台零占用这条线不能破）。
    /// </summary>
    public sealed class LogThrottle
    {
        /// <summary>节流窗口。60 秒是用户要求的粒度，也刚好是「一条 WARN 只占一屏」的量级。</summary>
        private const int WindowMs = 60_000;

        /// <summary>
        /// 表的容量上限。key 里带进程名，理论上无界（每次启动都是一批新名字）。
        /// 满了整表清空：清空最多让下一批各多记一行，不会让节流失效。
        /// </summary>
        private const int MaxKeys = 256;

        private sealed class Bucket
        {
            public long WindowStartMs;
            public int Suppressed;
        }

        private readonly Dictionary<string, Bucket> Buckets =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object Gate = new();

        /// <summary>
        /// 申请一次写日志的许可。
        /// </summary>
        /// <param name="key">节流键，约定为「类别|主体|错误码」。</param>
        /// <param name="suppressed">上一个窗口内被压掉的条数（0 = 没有）。</param>
        /// <returns>true = 这次应当写；false = 已被节流，调用方直接丢弃这条日志。</returns>
        public bool TryAcquire(string key, out int suppressed)
        {
            suppressed = 0;

            long now = Environment.TickCount64;

            lock (Gate)
            {
                Buckets.TryGetValue(key, out Bucket? bucket);

                if (bucket != null && now - bucket.WindowStartMs < WindowMs)
                {
                    bucket.Suppressed++;
                    return false;
                }

                // 上一个窗口压掉的条数交给这一条带出去
                if (bucket != null)
                    suppressed = bucket.Suppressed;

                if (bucket == null && Buckets.Count >= MaxKeys)
                    Buckets.Clear();

                Buckets[key] = new Bucket { WindowStartMs = now, Suppressed = 0 };
                return true;
            }
        }

        /// <summary>
        /// 清掉某个 key 的窗口。用于「同一个对象下一次成功了」的场合：
        /// 成功过就说明它现在可达，后面若再失败应当立刻能看到，而不是等窗口过期。
        /// </summary>
        public void Reset(string key)
        {
            lock (Gate)
                Buckets.Remove(key);
        }

        /// <summary>拼在日志末尾的「被抑制」后缀。没有抑制时返回空串。</summary>
        public static string SuppressedSuffix(int suppressed) =>
            suppressed > 0 ? $"（另有 {suppressed} 条相同错误已抑制）" : string.Empty;
    }
}
