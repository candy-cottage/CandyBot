using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Utilities;

/// <summary>
/// 调校身份纯函数库：提供字符串归一化、调校师列解析、以及用于比较的稳定 TunerKey。
/// 所有方法均为无副作用的纯函数，不依赖数据库或 I/O。
/// </summary>
public static partial class ForzaTuneIdentity
{
    // §1: 匹配两个及以上连续空白（用于归一化时折叠内部空白）
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleWhitespace();

    // §2 规则2: 整串纯数字括号，如 [681478665]，内部允许空白
    [GeneratedRegex(@"^\s*\[\s*[0-9][0-9\s]*\]\s*$")]
    private static partial Regex PureNumericBracket();

    // §2 规则4: 匹配任意括号组（全角/半角方括号与圆括号均接受）
    [GeneratedRegex(@"[\[\(（][^\]\)）]*[\]\)）]")]
    private static partial Regex BracketGroup();

    // §2 规则3: 分隔符集合（换行、全角逗号、半角逗号、顿号）
    private static readonly char[] Separators = ['\n', '，', ',', '、'];

    /// <summary>
    /// §1 归一化：null/空串 → ""；否则 Trim → 折叠内部连续空白 → NFKC（全角转半角）→ ToLowerInvariant。
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";

        // Trim 首尾空白
        string s = raw.Trim();
        // 折叠内部连续空白为单个 ASCII 空格
        s = MultipleWhitespace().Replace(s, " ");
        // NFKC 归一化：全角字母数字 → 半角，并转小写
        s = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return s;
    }

    /// <summary>
    /// §2 解析调校师列（spreadsheet 单元格）为有序 TunerToken 列表。
    /// - null/空串 → 空列表
    /// - 整串纯数字括号（如 [681478665]）→ 单 token，Id 为括号内数字，Keyword = null
    /// - 否则按 \n / 全角逗号 / 半角逗号 / 顿号分割，每段提取最后一个括号组为 Keyword，Id 为去掉所有括号组后的剩余文本
    /// - 按 IdNormalized 去重（保留首次出现），IdNormalized 为空的 token 丢弃
    /// 返回顺序为源顺序。
    /// </summary>
    public static IReadOnlyList<TunerToken> ParseTuners(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
            return [];

        string trimmed = cell.Trim();

        // 规则2: 整串纯数字括号 → 单 token
        if (PureNumericBracket().IsMatch(trimmed))
        {
            // 提取括号内的数字，去掉内部空白
            string digits = trimmed.TrimStart('[', ' ', '\t').TrimEnd(']', ' ', '\t');
            // 移除内部可能有的空格（括号内允许有空白）
            digits = string.Concat(digits.Where(char.IsAsciiDigit));
            return [new TunerToken(digits, Normalize(digits), null)];
        }

        // 规则3: 按分隔符分割
        string[] parts = trimmed.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        List<TunerToken> result = [];
        HashSet<string> seen = [];

        foreach (string part in parts)
        {
            // 规则4: 找出所有括号组
            MatchCollection matches = BracketGroup().Matches(part);

            string? keyword = null;
            if (matches.Count > 0)
            {
                // Keyword = 最后一个括号组的内容（去掉括号本身）
                string lastMatch = matches[^1].Value;
                // 去掉首尾括号字符（全角/半角均可能）
                keyword = lastMatch[1..^1].Trim();
                if (keyword.Length == 0) keyword = null;
            }

            // Id = 移除所有括号组后，折叠空白并 Trim
            string idRaw = BracketGroup().Replace(part, "");
            // 折叠空白
            idRaw = MultipleWhitespace().Replace(idRaw.Trim(), " ").Trim();

            string idNorm = Normalize(idRaw);
            if (idNorm.Length == 0) continue;

            // 按 IdNormalized 去重
            if (!seen.Add(idNorm)) continue;

            result.Add(new TunerToken(idRaw, idNorm, keyword));
        }

        return result;
    }

    /// <summary>
    /// §3 TunerKey：将所有 IdNormalized 按序数排序后以 U+0001 连接，用于身份比较。
    /// 使同一批调校师无论书写顺序如何都产生相同的键。
    /// </summary>
    public static string TunerKey(IReadOnlyList<TunerToken> tokens)
    {
        if (tokens.Count == 0) return "";
        string[] ids = [..tokens.Select(t => t.IdNormalized)];
        Array.Sort(ids, StringComparer.Ordinal);
        return string.Join('\x01', ids);
    }

    /// <summary>
    /// §6 ModelKey：制造商 + 型号 + 年份（归一化）以 U+0001 连接的车型键。
    /// Year 为 null 或 "" 时均归一化为空串，因此不产生额外区分度。
    /// </summary>
    public static string BuildModelKey(string? manufacturer, string? model, string? year) =>
        string.Join('\x01', [Normalize(manufacturer), Normalize(model), Normalize(year ?? "")]);

    /// <summary>
    /// §4 SourceIdentity：在写入数据库时冻结的调校行身份标识，用于跨同步轮次定位同一行。
    /// 由 modelKey 起头，接 6 个归一化配置字段（不含 SurfaceCondition/Strength 等展示字段），
    /// 再接 fileId，全部以 U+0001 连接。fileId 保证跨表不会误匹配。
    /// 前提：modelKey 必须先由 BuildModelKey 计算；调用方负责对哨兵/迁移行存入 ""。
    /// </summary>
    public static string BuildSourceIdentity(VehicleTuneRequest tune, string modelKey, string fileId)
    {
        string[] components =
        [
            modelKey,
            Normalize(tune.Class),
            Normalize(tune.MatchType),
            Normalize(tune.Drivetrain),
            Normalize(tune.PerformancePreference),
            Normalize(tune.TireCompound),
            TunerKey(ParseTuners(tune.TuneRaw)),
        ];
        return string.Join('\x01', components) + '\x01' + fileId;
    }

    /// <summary>
    /// §5 SourceFingerprint：SHA-256（小写十六进制）覆盖所有面向用户展示的原始字段（含 SurfaceCondition、
    /// Strength 等 identity 刻意排除的字段），以便在 identity 不变的前提下检测源内容是否变动。
    /// 使用原始（未归一化）值，确保任何字符变更（包括大小写、标点）都能翻转指纹。
    /// modelKey 参与计算，避免 identity 相同但来自不同车型的误相等。
    /// </summary>
    public static string BuildFingerprint(VehicleTuneRequest tune, string modelKey)
    {
        string[] parts =
        [
            modelKey,
            tune.Class ?? "",
            tune.MatchType ?? "",
            tune.Drivetrain ?? "",
            tune.PerformancePreference ?? "",
            tune.TireCompound ?? "",
            tune.TuneRaw ?? "",
            tune.SurfaceCondition ?? "",
            tune.Strength ?? "",
            tune.Remarks ?? "",
            tune.LapTime ?? "",
        ];
        string joined = string.Join('\x01', parts);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>
/// 单个调校师 token：原始 Id、归一化 Id、可选关键字（括号内容）。
/// </summary>
public record TunerToken(string Id, string IdNormalized, string? Keyword);
