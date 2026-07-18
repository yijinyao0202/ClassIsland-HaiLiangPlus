using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace ClassIsland.HaiGaoDuty.Services;

/// <summary>
/// 从常见表格或文本名单中提取姓名。导入结果只供用户确认，不会绕过现有名单校验。
/// </summary>
internal static partial class RosterImportService
{
    private const int MaxFileBytes = 20 * 1024 * 1024;
    private const int MaxRowsPerSheet = 5_000;
    private const int MaxColumns = 64;

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static RosterImportService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static RosterImportResult ImportFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return RosterImportResult.Failed("未找到要导入的文件。");
        }

        var file = new FileInfo(filePath);
        if (file.Length > MaxFileBytes)
        {
            return RosterImportResult.Failed("文件超过 20 MB，无法安全导入。请先只保留名单所在的工作表或文本。");
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" or ".xls" or ".xlsm" or ".xlsb" => ImportExcel(filePath),
            ".csv" or ".tsv" or ".txt" => ImportTextContent(ReadTextFile(filePath), Path.GetFileName(filePath)),
            _ => RosterImportResult.Failed("仅支持 Excel（.xlsx/.xls/.xlsm/.xlsb）和文本（.csv/.tsv/.txt）文件。")
        };
    }

    internal static RosterImportResult ImportTextContent(string text, string sourceName = "文本") =>
        SelectNames(ParseTextRows(text), sourceName);

    private static RosterImportResult ImportExcel(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            RosterImportResult? best = null;
            do
            {
                var rows = new List<IReadOnlyList<string>>();
                while (reader.Read() && rows.Count < MaxRowsPerSheet)
                {
                    var values = new string[Math.Min(reader.FieldCount, MaxColumns)];
                    for (var column = 0; column < values.Length; column++)
                    {
                        values[column] = FormatCell(reader.GetValue(column));
                    }

                    if (values.Any(value => !string.IsNullOrWhiteSpace(value)))
                    {
                        rows.Add(values);
                    }
                }

                var sheetName = string.IsNullOrWhiteSpace(reader.Name) ? "未命名工作表" : reader.Name;
                var candidate = SelectNames(rows, $"工作表“{sheetName}”");
                if (candidate.Names.Count > (best?.Names.Count ?? 0) ||
                    candidate.Score > (best?.Score ?? int.MinValue))
                {
                    best = candidate;
                }
            } while (reader.NextResult());

            return best ?? RosterImportResult.Failed("Excel 中没有可读取的工作表。");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException)
        {
            return RosterImportResult.Failed($"无法读取 Excel：{exception.Message}");
        }
    }

    private static string ReadTextFile(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            return Encoding.Unicode.GetString(bytes);
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    private static List<IReadOnlyList<string>> ParseTextRows(string text)
    {
        var lines = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(MaxRowsPerSheet)
            .ToList();
        var delimiter = DetectDelimiter(lines);
        return lines
            .SelectMany(line => delimiter is null
                ? SplitPlainTextLine(line)
                : [(IReadOnlyList<string>)SplitDelimitedLine(line, delimiter.Value)])
            .ToList();
    }

    private static IEnumerable<IReadOnlyList<string>> SplitPlainTextLine(string line)
    {
        var normalized = NormalizeCell(line);
        var parts = WhitespaceRegex().Split(normalized)
            .Select(NormalizeCell)
            .Where(part => part.Length > 0)
            .ToList();
        return parts.Count > 1 && parts.All(LooksLikeName)
            ? parts.Select(part => (IReadOnlyList<string>)[part])
            : [(IReadOnlyList<string>)[normalized]];
    }

    private static char? DetectDelimiter(IReadOnlyList<string> lines)
    {
        var candidates = new[] { '\t', ',', '，', ';', '；', '|' };
        var winner = candidates
            .Select(character => new
            {
                Character = character,
                Hits = lines.Take(100).Count(line => line.Contains(character))
            })
            .OrderByDescending(item => item.Hits)
            .FirstOrDefault();
        return winner is { Hits: > 0 } ? winner.Character : null;
    }

    private static IReadOnlyList<string> SplitDelimitedLine(string line, char delimiter)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append(character);
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == delimiter && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private static RosterImportResult SelectNames(IReadOnlyList<IReadOnlyList<string>> rows, string source)
    {
        if (rows.Count == 0)
        {
            return RosterImportResult.Failed($"{source}中没有内容。");
        }

        var columnCount = rows.Max(row => row.Count);
        ColumnCandidate? best = null;
        for (var column = 0; column < columnCount; column++)
        {
            var cells = rows
                .Select(row => column < row.Count ? NormalizeCell(row[column]) : string.Empty)
                .Where(cell => cell.Length > 0)
                .ToList();
            if (cells.Count == 0)
            {
                continue;
            }

            var header = cells.Take(5).FirstOrDefault(IsNameHeader);
            var metadataHeader = cells.Take(5).Any(IsMetadataHeader);
            var names = cells.Where(LooksLikeName).ToList();
            if (names.Count == 0)
            {
                continue;
            }

            var uniqueCount = names.Distinct(NameComparer).Count();
            var duplicateCount = names.Count - uniqueCount;
            var score = names.Count * 12 + uniqueCount * 3 - duplicateCount * 12;
            score += header is null ? 0 : 120;
            score -= metadataHeader ? 120 : 0;
            if (best is null || score > best.Score)
            {
                best = new ColumnCandidate(column, names, score);
            }
        }

        if (best is null || best.Score <= 0)
        {
            return RosterImportResult.Failed($"未能从{source}识别出姓名列。请将名单整理为“一行一个姓名”后再导入。", source);
        }

        var duplicate = best.Names
            .GroupBy(name => name, NameComparer)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        var message = $"已从{source}的第 {best.Column + 1} 列识别出 {best.Names.Count} 个姓名。";
        if (duplicate is not null)
        {
            message += $"其中“{duplicate}”重复，应用名单时会要求处理。";
        }

        return new RosterImportResult(best.Names, source, message, best.Score);
    }

    private static string FormatCell(object? value) => value switch
    {
        null => string.Empty,
        DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static string NormalizeCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC)
            .Replace("\u00A0", " ", StringComparison.Ordinal)
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Trim();
        normalized = ListPrefixRegex().Replace(normalized, string.Empty);
        return WhitespaceRegex().Replace(normalized, " ");
    }

    private static bool LooksLikeName(string value)
    {
        if (value.Length is < 1 or > 32 || IsNameHeader(value) || IsMetadataHeader(value))
        {
            return false;
        }

        if (value.All(char.IsDigit) || PhoneOrIdRegex().IsMatch(value) || value.Contains("班", StringComparison.Ordinal))
        {
            return false;
        }

        return value.Any(char.IsLetter);
    }

    private static bool IsNameHeader(string value) =>
        value.Contains("姓名", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("名字", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("学生", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("名单", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("人员", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("name", StringComparison.OrdinalIgnoreCase);

    private static bool IsMetadataHeader(string value) =>
        value.Contains("学号", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("序号", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("班级", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("年级", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("电话", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("身份证", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("成绩", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("分数", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("备注", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("性别", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("日期", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("^(?:\\d+[.、)】]|[（(]\\d+[）)])\\s*")]
    private static partial Regex ListPrefixRegex();

    [GeneratedRegex("^(?:1\\d{10}|\\d{15,18}[0-9Xx]?)$")]
    private static partial Regex PhoneOrIdRegex();

    private sealed record ColumnCandidate(int Column, IReadOnlyList<string> Names, int Score);
}

internal sealed record RosterImportResult(IReadOnlyList<string> Names, string Source, string Message, int Score)
{
    public static RosterImportResult Failed(string message, string source = "") => new([], source, message, int.MinValue);
}
