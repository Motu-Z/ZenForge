using System;
using System.IO;
using System.Text.Json;

namespace Zenwik.Core.Helpers
{
    /// <summary>
    /// 配置类文件的读写。四个 store 都用同一套约定：
    /// 读取失败由调用方兜底，写入一律「先写 .tmp 再原子替换」——
    /// 直接覆盖原文件时，写到一半崩溃或被强杀会留下半截 JSON，下次启动就读不出来。
    /// </summary>
    internal static class JsonFile
    {
        /// <summary>统一的可读格式：这些文件都是给人排查用的，不需要紧凑。</summary>
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        /// <summary>文件不存在或内容为 null 时返回 null，交给调用方决定怎么处理。</summary>
        public static T? Read<T>(string path) where T : class
        {
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }

        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, true);
        }
    }
}
