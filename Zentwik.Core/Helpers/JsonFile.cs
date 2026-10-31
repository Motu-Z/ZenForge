using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 配置类文件的读写。
    ///
    /// 所有调用都必须传入 <see cref="JsonTypeInfo{T}"/>（来自源生成上下文），
    /// 不允许使用 <c>JsonSerializer.Deserialize&lt;T&gt;(string)</c> 这类反射重载：
    /// 那些重载在 Native AOT 下没有实现，运行时抛异常而不是编译报错。
    ///
    /// 写入一律「先写 .tmp 再原子替换」：直接覆盖时，写到一半崩溃或被强杀
    /// 会留下半截 JSON，下次启动就读不出来。
    /// </summary>
    internal static class JsonFile
    {
        /// <summary>文件不存在时返回 null，交给调用方决定怎么处理。</summary>
        public static T? Read<T>(string path, JsonTypeInfo<T> typeInfo) where T : class
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentNullException.ThrowIfNull(typeInfo);

            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
        }

        public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentNullException.ThrowIfNull(typeInfo);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, typeInfo));
            File.Move(temp, path, true);
        }
    }
}
