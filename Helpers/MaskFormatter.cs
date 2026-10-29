using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ZenForge.Helpers
{
    /// <summary>
    /// 逻辑核掩码与可读文本的双向转换。
    ///
    /// 设计目标：让用户能像 CPU Sets 那样直接给一个掩码，而不是只能选 CCD0/CCD1。
    /// 支持三种输入写法（解析时自动识别）：
    ///   1. 十六进制：0x0000FFFF / FFFF
    ///   2. 区间列表：0-7,16-23     （逗号分隔，区间用连字符）
    ///   3. 枚举列表：0,1,2,3
    ///
    /// 输出统一为「区间列表」（0-7,16-23），比纯十六进制更易读；
    /// 同时提供十六进制形式用于悬停提示。
    /// </summary>
    public static class MaskFormatter
    {
        /// <summary>位掩码转「区间列表」文本，如 0x00FF00FF → "0-7,16-23"。</summary>
        public static string ToRangeText(ulong mask)
        {
            if (mask == 0)
                return "无";

            List<string> parts = new();
            int i = 0;

            while (i < 64)
            {
                // 跳过 0 位
                while (i < 64 && (mask & (1UL << i)) == 0)
                    i++;

                if (i >= 64)
                    break;

                int start = i;
                while (i < 64 && (mask & (1UL << i)) != 0)
                    i++;

                int end = i - 1;
                parts.Add(start == end ? $"{start}" : $"{start}-{end}");
            }

            return string.Join(",", parts);
        }

        /// <summary>位掩码转十六进制文本（按实际有效位数补零到 4 的倍数）。</summary>
        public static string ToHexText(ulong mask)
        {
            if (mask == 0)
                return "0x0";

            // 有效位数 = floor(log2(mask)) + 1，再向上取整到 4 的倍数
            int highestBit = 0;
            for (int i = 63; i >= 0; i--)
            {
                if ((mask & (1UL << i)) != 0)
                {
                    highestBit = i;
                    break;
                }
            }

            int hexDigits = (highestBit + 1 + 3) / 4;   // 16 位 → 4 个十六进制字符
            hexDigits = Math.Max(1, hexDigits);

            return "0x" + mask.ToString("X" + hexDigits);
        }

        /// <summary>同时给出区间与十六进制，用于 ToolTip。</summary>
        public static string ToFullText(ulong mask)
            => mask == 0 ? "无" : $"{ToRangeText(mask)}（{ToHexText(mask)}）";

        /// <summary>
        /// 解析用户输入的掩码文本。
        /// 返回 true 表示解析成功，mask 为结果，error 为失败原因。
        /// </summary>
        public static bool TryParse(string? text, out ulong mask, out string? error)
        {
            mask = 0;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "掩码不能为空";
                return false;
            }

            string input = text.Trim();

            // CCD0 / CCD1 是语义关键字，绝不能被当成十六进制数字
            // （"CCD0" 恰好全是合法 hex 字符，不拦截会解析成 0xCCD0）
            if (input.StartsWith("CCD", StringComparison.OrdinalIgnoreCase))
            {
                error = $"“{input}”是 CCD 关键字，请直接使用 CCD0 / CCD1，或输入掩码（如 0x0000FFFF）";
                return false;
            }

            // ---------- 形式 1：十六进制 ----------
            string hexCandidate = input;
            if (hexCandidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hexCandidate = hexCandidate.Substring(2);

            bool looksLikeHex =
                input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                (hexCandidate.Length > 0 &&
                 !input.Contains(',') &&
                 !input.Contains('-') &&
                 hexCandidate.All(Uri.IsHexDigit) &&
                 hexCandidate.Any(c => char.IsLetter(c)));

            if (looksLikeHex)
            {
                if (!ulong.TryParse(hexCandidate, System.Globalization.NumberStyles.HexNumber,
                                    null, out mask))
                {
                    error = $"“{input}”不是合法的十六进制掩码";
                    return false;
                }

                return Validate(mask, out error);
            }

            // ---------- 形式 2/3：区间或枚举列表 ----------
            foreach (string rawPart in input.Split(new[] { ',', '，', ' ', ';', '；' },
                                                   StringSplitOptions.RemoveEmptyEntries))
            {
                string part = rawPart.Trim();
                if (part.Length == 0)
                    continue;

                int dash = part.IndexOf('-');
                if (dash > 0)
                {
                    // 区间 a-b
                    string left = part.Substring(0, dash).Trim();
                    string right = part.Substring(dash + 1).Trim();

                    if (!int.TryParse(left, out int from) || !int.TryParse(right, out int to))
                    {
                        error = $"区间“{part}”格式不正确，应为 起始-结束";
                        return false;
                    }

                    if (from < 0 || to > 63 || from > to)
                    {
                        error = $"区间“{part}”超出 0-63 范围或起止颠倒";
                        return false;
                    }

                    for (int i = from; i <= to; i++)
                        mask |= 1UL << i;
                }
                else
                {
                    // 单个逻辑核
                    if (!int.TryParse(part, out int core))
                    {
                        error = $"“{part}”既不是数字也不是区间";
                        return false;
                    }

                    if (core < 0 || core > 63)
                    {
                        error = $"逻辑核 {core} 超出 0-63 范围";
                        return false;
                    }

                    mask |= 1UL << core;
                }
            }

            return Validate(mask, out error);
        }

        private static bool Validate(ulong mask, out string? error)
        {
            error = null;
            if (mask == 0)
            {
                error = "掩码不能为 0（至少选择一个逻辑核）";
                return false;
            }

            return true;
        }

        /// <summary>把掩码展开为逻辑核索引列表。</summary>
        public static IReadOnlyList<int> ToIndices(ulong mask)
        {
            List<int> list = new();
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) != 0)
                    list.Add(i);
            }

            return list;
        }
    }
}
