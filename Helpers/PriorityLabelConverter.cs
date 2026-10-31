using System;
using System.Globalization;
using System.Windows.Data;
using Zenwik.Services;

namespace Zenwik.Helpers
{
    /// <summary>
    /// 优先级档位 → 中文显示名。
    ///
    /// 【为什么不直接把下拉选项换成中文】
    /// <see cref="ProcessPriority.High"/> 这些常量是**持久化值**：绑核规则存在
    /// binding.json 里、日志里、旧版本写下的规则里，全是 "High" / "Normal"。
    /// 把常量改成中文等于让旧配置文件全部失效（读出来匹配不上任何档位，
    /// 一律回落成默认），为了一个显示问题动数据格式不划算。
    ///
    /// 所以显示层单独走一层转换：数据仍是英文枚举值，界面上是中文。
    /// 顺带解决另一半问题 —— 日志里写「→ High」用户看不懂，写「→ 高」才像人话，
    /// 那条走 <see cref="ProcessPriority.DisplayOf"/>，与这里同一份映射。
    /// </summary>
    public sealed class PriorityLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string label ? ProcessPriority.DisplayOf(label) : string.Empty;

        /// <summary>
        /// 反向不需要：下拉的 SelectedItem 绑的是原始值（字符串本身），
        /// 转换器只负责「怎么画」，不参与选择语义。
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
