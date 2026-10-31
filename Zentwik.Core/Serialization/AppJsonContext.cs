using System.Collections.Generic;
using System.Text.Json.Serialization;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Core.Serialization
{
    /// <summary>
    /// 配置文件的 JSON 源生成上下文。
    ///
    /// Native AOT 下没有运行时代码生成，<c>JsonSerializer.Deserialize&lt;T&gt;</c> 这类
    /// 基于反射的重载会被裁剪掉，调用点直接抛异常。所有配置文件类型必须在这里登记，
    /// 由编译器在构建期生成读写代码。
    ///
    /// 【命名策略不能改】
    /// 现有 ui.json / tuning.json / bindings.json 的键都是 PascalCase。
    /// 这里一旦加上 <c>PropertyNamingPolicy = CamelCase</c>，程序不会报错，
    /// 只会让老用户的所有配置读不出来并静默回落成默认值 ——
    /// 用户以为设置被重置了，实际上文件还在。
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        GenerationMode = JsonSourceGenerationMode.Default)]
    [JsonSerializable(typeof(UiSettings))]
    [JsonSerializable(typeof(TuningProfile))]
    [JsonSerializable(typeof(BindingProfile))]
    [JsonSerializable(typeof(List<CustomMask>))]
    [JsonSerializable(typeof(ProfileFile))]
    [JsonSerializable(typeof(ScenarioFile))]
    internal sealed partial class AppJsonContext : JsonSerializerContext
    {
    }
}
