#:property PublishAot=false
// Сверяет утверждения с кодом. Доказывает только то, что цитата есть в указанном месте,
// а не то, что она подтверждает утверждение.
// Установка: dotnet publish tools/check-claims.cs -o "$HOME/.claude/tools/check-claims"
// Запуск из корня репозитория: dotnet "$HOME/.claude/tools/check-claims/check-claims.dll" claims.json
// Код выхода: 0 всё сходится, 3 есть расхождения, 2 неверный вход. Итог действителен только при строке «Всего:».
//
// claims.json:
// [
//   {"claim":"...","status":"CONFIRMED","file":"src/A.cs","line":42,"quote":"точный текст из кода"},
//   {"claim":"...","status":"REFUTED","file":"src/A.cs","line":7,"quote":"текст, который опровергает","ref":"origin/release"},
//   {"claim":"...","status":"NOT_FOUND","pattern":"Foo(","path":"src"},
//   {"claim":"...","status":"UNVERIFIED","reason":"почему проверить не удалось"}
// ]
// ref необязателен: файл читается из этой ветки или коммита, а не с диска.
// NOT_FOUND: git grep ищет pattern как точный текст (в path или во всём репозитории); совпадение считается расхождением.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const int LineTolerance = 3;
const int MinQuoteLength = 12;
const int MinReasonLength = 10;
const int MinPatternLength = 3;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length != 1) return Fail("Укажите путь к claims.json.");

JsonDocument doc;
try { doc = JsonDocument.Parse(File.ReadAllText(args[0])); }
catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
{
    return Fail($"Не удалось прочитать {args[0]}: {e.Message}");
}

using (doc)
{
    if (doc.RootElement.ValueKind != JsonValueKind.Array) return Fail("Корень JSON должен быть массивом.");

    var counts = new SortedDictionary<string, int>();
    var problems = new List<string>();
    var number = 0;

    foreach (var item in doc.RootElement.EnumerateArray())
    {
        number++;
        try
        {
        var claim = Text(item, "claim");
        var status = Text(item, "status");
        var label = $"#{number} {(claim.Length > 70 ? claim[..70] + "…" : claim)}";
        counts[status.Length == 0 ? "(нет статуса)" : status] = counts.GetValueOrDefault(status.Length == 0 ? "(нет статуса)" : status) + 1;

        if (claim.Length == 0) { problems.Add($"{label}: нет текста утверждения"); continue; }

        switch (status)
        {
            case "CONFIRMED":
            case "REFUTED":
                var problem = CheckEvidence(item, args[0]);
                if (problem is not null) problems.Add($"{label}: {problem}");
                break;
            case "NOT_FOUND":
                if (CheckAbsence(item) is { } hit) problems.Add($"{label}: {hit}");
                break;
            case "UNVERIFIED":
                if (Text(item, "reason").Length < MinReasonLength) problems.Add($"{label}: не указана причина (reason)");
                break;
            default:
                problems.Add($"{label}: неизвестный статус '{status}'");
                break;
        }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            problems.Add($"#{number}: сбой проверки: {e.Message}");
        }
    }

    problems.ForEach(Console.WriteLine);
    Console.WriteLine($"Всего: {number}. " + string.Join(", ", counts.Select(c => $"{c.Key}: {c.Value}")) + $". Расхождений: {problems.Count}.");
    if (number == 0) return Fail("Список утверждений пуст.");
    return problems.Count == 0 ? 0 : 3;
}

static string? CheckEvidence(JsonElement item, string claimsPath)
{
    var file = Text(item, "file");
    var quote = Text(item, "quote");
    var gitRef = Text(item, "ref");
    if (file.Length == 0) return "нет файла (file)";
    if (Path.GetFullPath(file) == Path.GetFullPath(claimsPath)) return "ссылка на сам файл утверждений";
    if (!item.TryGetProperty("line", out var lineElement) || lineElement.ValueKind != JsonValueKind.Number || !lineElement.TryGetInt32(out var line) || line < 1) return "нет номера строки (line)";
    var normalizedQuote = Norm(quote);
    if (normalizedQuote.Length < MinQuoteLength) return $"цитата короче {MinQuoteLength} символов";

    if (item.TryGetProperty("ref", out var refElement) && refElement.ValueKind != JsonValueKind.String) return "ref должен быть строкой";
    if (gitRef.StartsWith('-') || gitRef.Any(char.IsWhiteSpace) || gitRef.Contains(':')) return $"в ref должна быть ветка или коммит, а указано '{gitRef}'";

    var (lines, readError) = ReadLines(file, gitRef);
    if (lines is null) return readError;
    var place = gitRef.Length > 0 ? $"{gitRef}:{file}" : file;
    if (line > lines.Length) return $"в {place} всего {lines.Length} строк, указана {line}";

    // Цитата может занимать несколько строк: проверяем окна от указанной строки.
    var quoteLines = quote.Split('\n').Length;
    var from = Math.Max(0, line - 1 - LineTolerance);
    var to = Math.Min(lines.Length - 1, line - 1 + LineTolerance);
    for (var start = from; start <= to; start++)
    {
        var window = string.Join(" ", lines.Skip(start).Take(Math.Max(quoteLines, 1) + 8));
        if (Norm(lines[start]).Contains(normalizedQuote) || (quoteLines > 1 && QuoteStartsOnFirstLine(window, lines[start], normalizedQuote))) return null;
    }

    for (var i = 0; i < lines.Length; i++)
        if (Norm(lines[i]).Contains(normalizedQuote) || (quoteLines > 1 && QuoteStartsOnFirstLine(string.Join(" ", lines.Skip(i).Take(quoteLines + 8)), lines[i], normalizedQuote))) return $"цитата найдена в строке {i + 1}, а указана {line}";
    return $"цитаты нет в {place}";
}

static (string[]? Lines, string? Error) ReadLines(string file, string gitRef)
{
    if (gitRef.Length == 0)
    {
        if (!File.Exists(file)) return (null, $"файл {file} не найден");
        return (SplitLines(Decode(File.ReadAllBytes(file))), null);
    }

    var (code, output, error) = Git("show", "--end-of-options", $"{gitRef}:{file.Replace('\\', '/')}");
    if (code != 0) return (null, $"git show {gitRef}:{file}: {error.Trim()}");
    return (SplitLines(Decode(output)), null);
}

static string? CheckAbsence(JsonElement item)
{
    var pattern = Text(item, "pattern");
    var path = Text(item, "path");
    var gitRef = Text(item, "ref");
    if (pattern.Length < MinPatternLength) return $"нет строки поиска (pattern) не короче {MinPatternLength} символов";
    if (item.TryGetProperty("ref", out var refElement) && refElement.ValueKind != JsonValueKind.String) return "ref должен быть строкой";
    if (gitRef.StartsWith('-') || gitRef.Any(char.IsWhiteSpace) || gitRef.Contains(':')) return $"в ref должна быть ветка или коммит, а указано '{gitRef}'";

    var gitArgs = new List<string> { "grep", "-n", "-I", "-F" };
    if (gitRef.Length == 0) gitArgs.Add("--untracked");
    gitArgs.Add("-e");
    gitArgs.Add(pattern);
    if (gitRef.Length > 0) gitArgs.Add(gitRef); // ref уже проверен: не начинается с «-»
    if (path.Length > 0) { gitArgs.Add("--"); gitArgs.Add(path.Replace('\\', '/')); }
    var (code, output, error) = Git(gitArgs.ToArray());
    if (code == 1) return null;
    if (code != 0) return $"git grep: {error.Trim()}";
    return $"строка поиска найдена: {SplitLines(Decode(output))[0]}";
}

static (int Code, byte[] Output, string Error) Git(params string[] arguments)
{
    var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
    using var process = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить git");
    var errorTask = process.StandardError.ReadToEndAsync();
    using var buffer = new MemoryStream();
    process.StandardOutput.BaseStream.CopyTo(buffer);
    process.WaitForExit();
    return (process.ExitCode, buffer.ToArray(), errorTask.Result);
}

static string Decode(byte[] b)
{
    if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
    if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
    if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
    try { return new UTF8Encoding(false, true).GetString(b); }
    catch (DecoderFallbackException)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251).GetString(b);
    }
}

static string[] SplitLines(string text)
{
    var lines = Regex.Split(text, "\r\n|\r|\n");
    return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
}

static bool QuoteStartsOnFirstLine(string window, string firstLine, string quote)
{
    var at = Norm(window).IndexOf(quote, StringComparison.Ordinal);
    return at >= 0 && at < Norm(firstLine).Length;
}

static string Text(JsonElement item, string name) =>
    item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()!.Trim()
        : "";

static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
