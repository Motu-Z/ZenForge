using System;
using Zentwik.Services;

namespace Zentwik
{
    /// <summary>
    /// 进程入口，取代 App.xaml 自动生成的 Main。
    ///
    /// 存在的唯一理由：让 -silent 走一条完全不加载 WPF 的路径。
    /// WPF 的 Application 一旦实例化，PresentationFramework / PresentationCore /
    /// WindowsBase / wpfgfx 就全部进进程，常驻内存直接翻倍；而开机自启要的
    /// 只是「后台绑核」，跟界面无关。
    ///
    /// 两条硬约束，违反任一条前功尽弃：
    ///   1. Main 方法体内不得出现任何 WPF 类型，也不得引用 App 类型
    ///      —— JIT 编译 Main 时会解析它直接引用的类型，而 App 的基类就是 Application。
    ///   2. 分派目标标 [MethodImpl(NoInlining)]，防止 JIT 内联后把 WPF 类型带进来。
    ///
    /// 所以下面这段看起来「只是转发了两个方法」，但每一个字都是刻意的：
    /// 判定逻辑在本地重写了一份，而不是复用 App.IsSilentLaunch。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // 容器必须最先建起来：下面的异常兜底一旦触发就要写日志，
            // 而 Svc 只是个转发器，没有容器就是空引用。
            // 此时界面尚未创建，注入的是待挂载的调度器（见 Composition.Dispatcher）。
            Svc.Initialize(Composition.Build());

            // 进程级兜底。这两道在任何宿主模式下都该存在 —— 0.8.0 的闪退之所以
            // 难定位，就是因为异常穿过 Application.Run 后没人接，日志里只剩
            // 一句「后台常驻失败」，断点在哪全靠猜。
            //
            // 这里只能用不依赖 WPF 的类型（AppDomain / TaskScheduler），
            // 否则纯后台路径的前提当场失效。
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Svc.Log.Error($"进程未处理异常（即将终止）：{e.ExceptionObject}");

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Svc.Log.Error($"未观察的任务异常：{e.Exception}");
                e.SetObserved();
            };

            return IsSilentLaunch(args)
                ? HeadlessHost.Run(args)
                : GuiHost.Start();
        }

        /// <summary>
        /// 判定 -silent。与 App.xaml.cs 里的同名逻辑保持一致，
        /// 但不能复用那边的实现：那是 App 的静态方法，引用 App 类型就等于加载 WPF。
        /// </summary>
        private static bool IsSilentLaunch(string[] args)
        {
            foreach (string arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    continue;

                string normalized = arg.Trim().TrimStart('-', '/', '—');
                if (string.Equals(normalized, "silent", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
