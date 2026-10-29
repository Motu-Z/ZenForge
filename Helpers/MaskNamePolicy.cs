using System;
using System.Collections.Generic;

namespace ZenForge.Helpers
{
    /// <summary>
    /// 掩码名称的校验策略（长度 + 重名）。名称上限此前散落在三处且互不同步：
    /// XAML 的 MaxLength、保存时的 Length 判断、<see cref="MaskStore.Upsert"/> 里的
    /// Substring 截断。这里把它们收敛成唯一来源，所有入口统一引用本类。
    /// </summary>
    /// <remarks>
    /// 【为什么用「权重」而不是「字符数」】
    /// 字符数上限在中英文下表现完全不同：16 个英文约 112px，16 个中文约 208px，
    /// 而左栏列表留给名称的宽度约 188px。同一个 16 的阈值，英文绰绰有余、中文必然溢出。
    /// 所以改成按显示宽度计权重：全角字符计 2，半角字符计 1。
    ///
    /// 用户确定的口径：上限 = 6 个中日韩文字或全角符号，或 12 个拉丁字母 / 数字 / 半角符号。
    /// 带变音符的拉丁字母（é、ü、ß 等）按半角计 1，不做特殊处理 —— 它们的宽度与
    /// 普通拉丁字母一致，单独加权会让规则难以解释。
    /// </remarks>
    internal static class MaskNamePolicy
    {
        /// <summary>总权重上限。12 = 6 个全角字符，或 12 个半角字符。</summary>
        public const int MaxWeight = 12;

        /// <summary>输入框下方的常驻提示，说明上限的换算方式。</summary>
        public const string Hint = "最多 6 个中文或 12 个英文";

        /// <summary>名称与他人重复时的统一提示文案。</summary>
        public const string DuplicateError = "这个名称已经用过了，请换一个。";

        /// <summary>单个字符的显示权重：全角 / 中日韩 / 代理对（emoji）计 2，其余计 1。</summary>
        public static int WeightOf(char c)
        {
            if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c))
                return 2;   // 代理对的一半，成对出现时由 Weight(string) 合并成一次计数

            int code = c;

            // 韩文字母（Hangul Jamo）
            if (code >= 0x1100 && code <= 0x115F) return 2;
            // CJK 部首补充、康熙部首、CJK 统一汉字及扩展 A
            if (code >= 0x2E80 && code <= 0xA4CF) return 2;
            // 韩文音节
            if (code >= 0xAC00 && code <= 0xD7A3) return 2;
            // CJK 兼容汉字
            if (code >= 0xF900 && code <= 0xFAFF) return 2;
            // CJK 兼容形式（竖排标点等）
            if (code >= 0xFE30 && code <= 0xFE4F) return 2;
            // 全角 ASCII 与全角符号
            if (code >= 0xFF00 && code <= 0xFF60) return 2;
            if (code >= 0xFFE0 && code <= 0xFFE6) return 2;

            return 1;
        }

        /// <summary>
        /// 文本的显示总权重。代理对（emoji）按一个字符计 2，不会算成两次。
        /// </summary>
        public static int Weight(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int total = 0;
            for (int i = 0; i < text.Length; i++)
            {
                // emoji 等增补平面字符占两个 char，跳过低代理，避免重复计数
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    total += 2;
                    i++;
                    continue;
                }
                total += WeightOf(text[i]);
            }
            return total;
        }

        /// <summary>
        /// 判断名称是否可用。空名称与超重分别给出不同的提示，便于界面精确定位问题。
        /// </summary>
        public static bool IsValid(string? text, out string error)
        {
            string name = (text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                error = "掩码名称为空。";
                return false;
            }

            int weight = Weight(name);
            if (weight > MaxWeight)
            {
                error = $"掩码名称过长：当前 {weight}／{MaxWeight}（{Hint}）。";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// 完整校验：长度 + 重名。重名比较**忽略大小写**——
        /// 左栏就是一串纯文字，「Game」与「game」在列表里几乎分不出是哪条，
        /// 允许并存只会让绑核规则指向一个用户自己也说不清的对象。
        /// </summary>
        /// <param name="text">待校验名称。</param>
        /// <param name="others">
        /// 会被判定为冲突的其它名称。调用方负责把「自己」从里面剔掉
        /// （见 <see cref="MaskStore.NamesExceptId"/>），本方法不做身份判断。
        /// </param>
        /// <param name="error">不可用时的原因。</param>
        public static bool IsValid(string? text, IEnumerable<string>? others, out string error)
        {
            if (!IsValid(text, out error))
                return false;

            string name = (text ?? string.Empty).Trim();

            if (others != null)
            {
                foreach (string other in others)
                {
                    if (string.IsNullOrWhiteSpace(other))
                        continue;

                    if (string.Equals(other.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        error = DuplicateError;
                        return false;
                    }
                }
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// 把文本裁到指定权重以内，返回不超过上限的最长前缀。
        ///
        /// 用于输入框的实时截断：超过上限就停在边界上，而不是等用户
        /// 打完再告诉他超了。代理对（emoji）整体保留或整体丢弃，不劈成半个。
        /// </summary>
        public static string TrimToWeight(string? text, int maxWeight)
        {
            if (string.IsNullOrEmpty(text) || maxWeight <= 0)
                return string.Empty;

            int total = 0;
            int cut = 0;

            for (int i = 0; i < text.Length; i++)
            {
                int step;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    step = 2;
                else
                    step = WeightOf(text[i]);

                if (total + step > maxWeight)
                    break;

                total += step;
                cut = i + ((step == 2 && char.IsHighSurrogate(text[i])) ? 2 : 1);

                if (step == 2 && char.IsHighSurrogate(text[i]))
                    i++;    // 跳过低位代理
            }

            return text.Substring(0, Math.Min(cut, text.Length));
        }
    }
}
