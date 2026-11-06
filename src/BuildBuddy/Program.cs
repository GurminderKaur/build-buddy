// Build Buddy — Day 1 starter
// Runs `dotnet build`, catches C# compiler errors, and asks a local model
// (via Ollama) to explain each one and suggest a fix. Fully offline.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var target = args.Length > 0 ? args[0] : ".";
var model = Environment.GetEnvironmentVariable("BUILDBUDDY_MODEL") ?? "gemma3:4b";
var ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434";

Console.WriteLine($"Building {target} ...");
var buildOutput = await RunDotnetBuild(target);
var errors = ParseErrors(buildOutput);

if (errors.Count == 0)
{
    Console.WriteLine("No C# compiler errors found. Nothing to explain.");
    return 0;
}

Console.WriteLine($"Found {errors.Count} error(s). Asking {model} ...\n");

using var http = new HttpClient
{
    BaseAddress = new Uri(ollamaUrl),
    Timeout = TimeSpan.FromMinutes(5)
};

foreach (var e in errors)
{
    var snippet = ReadSnippet(e.File, e.Line, context: 4);
    var prompt = $"""
        A C# project failed to build.

        Error: {e.Code} at line {e.Line}, column {e.Col}
        Message: {e.Message}

        Code around the error (">" marks the failing line):
        {snippet}

        In plain English, explain why this error happens (2 sentences max).
        Then give the smallest code change that fixes it, as a short code block.
        """;

    var sw = Stopwatch.StartNew();
    var answer = await AskOllama(http, model, prompt);
    sw.Stop();

    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"{e.Code}  {Path.GetFileName(e.File)}:{e.Line}  {e.Message}");
    Console.ResetColor();
    Console.WriteLine(answer.Trim());
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"({sw.Elapsed.TotalSeconds:F1}s with {model})\n");
    Console.ResetColor();
}

return 1;

// ---------- helpers ----------

static async Task<string> RunDotnetBuild(string target)
{
    var psi = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var a in new[] { "build", target, "--nologo", "-tl:off", "-clp:NoSummary" })
        psi.ArgumentList.Add(a);

    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    return await stdout + await stderr;
}

static List<BuildError> ParseErrors(string output)
{
    // e.g. /path/Program.cs(3,14): error CS0246: The type or namespace name 'StringBuilder' could not be found ... [/path/x.csproj]
    var rx = new Regex(
        @"^\s*(?<file>.+?)\((?<line>\d+),(?<col>\d+)\): error (?<code>CS\d{4}): (?<msg>.+?)(?: \[[^\]]+\])?\s*$",
        RegexOptions.Multiline);

    return rx.Matches(output)
        .Select(m => new BuildError(
            m.Groups["file"].Value.Trim(),
            int.Parse(m.Groups["line"].Value),
            int.Parse(m.Groups["col"].Value),
            m.Groups["code"].Value,
            m.Groups["msg"].Value.Trim()))
        .DistinctBy(e => (e.File, e.Line, e.Col, e.Code)) // MSBuild can repeat errors
        .ToList();
}

static string ReadSnippet(string file, int line, int context)
{
    if (!File.Exists(file)) return "(source file not found)";
    var lines = File.ReadAllLines(file);
    var start = Math.Max(1, line - context);
    var end = Math.Min(lines.Length, line + context);
    var sb = new StringBuilder();
    for (var i = start; i <= end; i++)
        sb.AppendLine($"{(i == line ? ">" : " ")} {i,4}: {lines[i - 1]}");
    return sb.ToString();
}

static async Task<string> AskOllama(HttpClient http, string model, string prompt)
{
    var body = new
    {
        model,
        stream = false,
        options = new { temperature = 0 },
        messages = new[]
        {
            new { role = "system", content = "You are a concise senior C#/.NET reviewer. Be specific to the code shown." },
            new { role = "user", content = prompt }
        }
    };

    using var resp = await http.PostAsJsonAsync("/api/chat", body);
    resp.EnsureSuccessStatusCode();
    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
}

record BuildError(string File, int Line, int Col, string Code, string Message);