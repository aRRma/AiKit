// Сводка по журналу сессии Claude Code: кто (главный агент и субагенты), на какой модели, сколько ходов и токенов,
// какие инструменты вызывал и сколько вызовов закончились ошибкой или запросом подтверждения.
// Установка: dotnet publish tools/session-report -c Release -o "$HOME/.claude/tools/session-report"
// Запуск:    dotnet "$HOME/.claude/tools/session-report/session-report.dll" <id сессии или путь к её .jsonl>
// Код выхода: 0 — сводка напечатана, 2 — сессия не найдена или аргумент неверный.
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length != 1) return Fail("Укажите id сессии или путь к её .jsonl.");

var main = FindSession(args[0]);
if (main is null) return Fail($"Сессия не найдена: {args[0]}");

var files = new List<string> { main };
var subagents = Path.Combine(Path.GetDirectoryName(main)!, Path.GetFileNameWithoutExtension(main), "subagents");
if (Directory.Exists(subagents)) files.AddRange(Directory.GetFiles(subagents, "agent-*.jsonl").Order());

var rows = files.Select(f => Read(f, f == main)).ToList();
Console.WriteLine("| Кто | Модель | Ходов | Новый ввод | Запись кеша | Чтение кеша | Вывод | LSP | MCP | Правки | ToolSearch | Bash | Ошибки | Запросы |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
foreach (var r in rows) Console.WriteLine(r.Line());
Console.WriteLine(Row.Sum(rows).Line());
return 0;

static string? FindSession(string arg)
{
    if (File.Exists(arg)) return Path.GetFullPath(arg);
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
    if (!Directory.Exists(root) || arg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
    return Directory.EnumerateFiles(root, arg + "*.jsonl", SearchOption.AllDirectories)
        .FirstOrDefault(f => !f.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar));
}

static Row Read(string file, bool isMain)
{
    var row = new Row { Who = isMain ? "главный" : AgentType(file) };
    var usage = new Dictionary<string, long[]>();
    var models = new SortedSet<string>();
    foreach (var line in File.ReadLines(file))
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
            var role = Text(message, "role");
            if (role == "assistant")
            {
                if (Text(message, "model") == "<synthetic>") continue;
                if (Text(message, "model") is { Length: > 0 } model) models.Add(model);
                if (message.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    usage[Text(message, "id")] = [Num(u, "input_tokens"), Num(u, "cache_creation_input_tokens"), Num(u, "cache_read_input_tokens"), Num(u, "output_tokens")];
            }
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = Text(item, "type");
                if (role == "assistant" && type == "tool_use") row.CountTool(Text(item, "name"));
                if (role == "user" && type == "tool_result" && item.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                {
                    var text = item.TryGetProperty("content", out var c) ? c.GetRawText() : "";
                    if (text.Contains("requires approval", StringComparison.OrdinalIgnoreCase) || text.Contains("has been denied", StringComparison.OrdinalIgnoreCase)) row.Approvals++;
                    else row.Errors++;
                }
            }
        }
    }
    row.Model = models.Count == 0 ? "—" : string.Join(", ", models);
    row.Turns = usage.Count;
    foreach (var u in usage.Values) { row.Input += u[0]; row.CacheWrite += u[1]; row.CacheRead += u[2]; row.Output += u[3]; }
    return row;
}

static string AgentType(string file)
{
    var meta = Path.ChangeExtension(file, ".meta.json");
    try
    {
        if (File.Exists(meta))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            if (Text(doc.RootElement, "agentType") is { Length: > 0 } type) return type;
        }
    }
    catch (Exception e) when (e is IOException or JsonException) { }
    return Path.GetFileNameWithoutExtension(file);
}

static string Text(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

static long Num(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

sealed class Row
{
    public string Who = "", Model = "";
    public long Turns, Input, CacheWrite, CacheRead, Output;
    public int Lsp, Mcp, Edits, ToolSearch, Bash, Errors, Approvals;

    public void CountTool(string name)
    {
        if (name == "LSP") Lsp++;
        else if (name.StartsWith("mcp__", StringComparison.Ordinal)) Mcp++;
        else if (name is "Edit" or "Write" or "NotebookEdit") Edits++;
        else if (name == "ToolSearch") ToolSearch++;
        else if (name is "Bash" or "PowerShell") Bash++;
    }

    public string Line() =>
        $"| {Who} | {Model} | {Turns} | {Input} | {CacheWrite} | {CacheRead} | {Output} | {Lsp} | {Mcp} | {Edits} | {ToolSearch} | {Bash} | {Errors} | {Approvals} |";

    public static Row Sum(List<Row> rows) => new()
    {
        Who = "**Всего**", Model = "",
        Turns = rows.Sum(r => r.Turns), Input = rows.Sum(r => r.Input), CacheWrite = rows.Sum(r => r.CacheWrite),
        CacheRead = rows.Sum(r => r.CacheRead), Output = rows.Sum(r => r.Output),
        Lsp = rows.Sum(r => r.Lsp), Mcp = rows.Sum(r => r.Mcp), Edits = rows.Sum(r => r.Edits),
        ToolSearch = rows.Sum(r => r.ToolSearch), Bash = rows.Sum(r => r.Bash), Errors = rows.Sum(r => r.Errors), Approvals = rows.Sum(r => r.Approvals),
    };
}
