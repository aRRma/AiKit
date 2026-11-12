#:property PublishAot=false
// Stop-хук: не даёт завершить ход, пока не проходят сборка и быстрые тесты.
// Установка: dotnet publish tools/stop-check.cs -o "$HOME/.claude/tools/stop-check"
// Аргументы: <решение относительно корня репозитория> [фильтр dotnet test]
// Код выхода: 0 можно завершать, 2 блокировать, 1 сбой настройки (ход не блокируется, сообщение в stderr).
using System.Diagnostics;
using System.Text.Json;

if (args.Length is < 1 or > 2) return Warn("Укажите решение и, при необходимости, фильтр тестов.");

// Хук уже блокировал этот ход: второй раз не блокируем, иначе агент не сможет остановиться и спросить.
if (Console.IsInputRedirected)
{
    try
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        if (input.RootElement.ValueKind == JsonValueKind.Object && input.RootElement.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True) return 0;
    }
    catch (JsonException) { }
}

try
{
    var (topCode, top, _) = Run("git", "rev-parse", "--show-toplevel");
    if (topCode != 0) return 0; // не репозиторий: проверять нечего
    Directory.SetCurrentDirectory(top.Trim());

    if (!File.Exists(args[0])) return Warn($"Не найдено решение {args[0]} в {top.Trim()}. Проверка не выполнена.");

    var marker = Run("git", "rev-parse", "--git-path", "verified-head").Output.Trim();
    var head = Run("git", "rev-parse", "HEAD").Output.Trim();
    var clean = Run("git", "status", "--porcelain").Output.Trim().Length == 0;
    var verified = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
    if (clean && head.Length > 0 && head == verified) return 0;

    var build = Run("dotnet", "build", args[0], "--no-incremental", "-warnaserror", "-v", "q");
    if (build.Code != 0) return Block("сборка", build);

    var testArgs = new List<string> { "test", args[0], "--no-build" };
    if (args.Length == 2) { testArgs.Add("--filter"); testArgs.Add(args[1]); }
    var test = Run("dotnet", testArgs.ToArray());
    if (test.Code != 0) return Block("тесты", test);

    if (clean && head.Length > 0) File.WriteAllText(marker, head);
    return 0;
}
catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
{
    return Warn($"Проверка не выполнена: {e.Message}");
}

static int Block(string step, (int Code, string Output, string Error) result)
{
    var text = (result.Output + result.Error).Trim();
    Console.Error.WriteLine(text.Length > 6000 ? text[^6000..] : text);
    // Непустой stdout нужен: Claude Code иначе может счесть код 2 ошибкой самого хука.
    Console.WriteLine($"Stop-check: не прошёл шаг «{step}», подробности в stderr.");
    return 2;
}

static int Warn(string message)
{
    Console.Error.WriteLine("Stop-check: " + message);
    return 1;
}

static (int Code, string Output, string Error) Run(string file, params string[] arguments)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
    using var process = Process.Start(psi) ?? throw new IOException($"Не удалось запустить {file}");
    var errorTask = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output, errorTask.Result);
}
