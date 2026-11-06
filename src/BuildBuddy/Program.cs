// Build Buddy — Day 1 starter
// Runs `dotnet build`, catches C# compiler errors, and asks a local model
// (via Ollama) to explain each one and suggest a fix. Fully offline.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var verify = args.Any(a => a == "--verify");
var positional = args.Where(a => a != "--verify").ToArray();
var target = positional.Length > 0 ? positional[0] : ".";
var model = Environment.GetEnvironmentVariable("BUILDBUDDY_MODEL") ?? "gemma3:4b";
var ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434";
var sampleName = GetSampleName(target);

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

var results = new List<VerifyResult>();

foreach (var e in errors)
{
    var snippet = ReadSnippet(e.File, e.Line, context: 4);
    var prompt = verify ? BuildVerifyPrompt(e, snippet) : BuildExplainPrompt(e, snippet);

    var sw = Stopwatch.StartNew();
    var answer = await AskOllama(http, model, prompt);

    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"{e.Code}  {Path.GetFileName(e.File)}:{e.Line}  {e.Message}");
    Console.ResetColor();
    Console.WriteLine(answer.Trim());

    if (verify)
    {
        var (fixedIt, detail) = await TryVerifyFix(target, e, answer);
        sw.Stop();
        Console.ForegroundColor = fixedIt ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(fixedIt
            ? $"Verify: original {e.Code} is gone."
            : $"Verify: original {e.Code} is still present. {detail}");
        Console.ResetColor();
        results.Add(new VerifyResult(sampleName, e.Code, fixedIt, sw.Elapsed.TotalSeconds));
        AppendCsv(model, sampleName, e.Code, fixedIt, sw.Elapsed.TotalSeconds);
    }
    else
    {
        sw.Stop();
    }

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"({sw.Elapsed.TotalSeconds:F1}s with {model})\n");
    Console.ResetColor();
}

if (verify)
    PrintSummary(results);

return 1;

// ---------- helpers ----------

static string BuildExplainPrompt(BuildError e, string snippet) => $"""
    A C# project failed to build.

    Error: {e.Code} at line {e.Line}, column {e.Col}
    Message: {e.Message}

    Code around the error (">" marks the failing line):
    {snippet}

    In plain English, explain why this error happens (2 sentences max).
    Then give the smallest code change that fixes it, as a short code block.
    """;

static string BuildVerifyPrompt(BuildError e, string snippet)
{
    var source = File.Exists(e.File) ? File.ReadAllText(e.File) : "(source file not found)";
    return $"""
        A C# project failed to build.

        Error: {e.Code} at line {e.Line}, column {e.Col}
        Message: {e.Message}

        Code around the error (">" marks the failing line):
        {snippet}

        Complete current source file ({Path.GetFileName(e.File)}):
        {source}

        In plain English, explain why this error happens (2 sentences max).
        Then return the COMPLETE corrected source file inside one csharp code block
        (not just a snippet). Do not omit any lines. Do not add extra files or commentary
        inside the code block.
        """;
}

static async Task<(bool Fixed, string Detail)> TryVerifyFix(string target, BuildError error, string answer)
{
    var corrected = ExtractCsharpFile(answer);
    if (corrected is null)
        return (false, "No csharp code block in the model reply.");

    var projectDir = GetProjectDir(target);
    var originalFull = Path.GetFullPath(error.File);
    var relative = Path.GetRelativePath(projectDir, originalFull);
    if (relative.StartsWith("..", StringComparison.Ordinal))
        return (false, "Failing file is outside the target project folder.");

    var tempDir = Path.Combine(Path.GetTempPath(), "build-buddy-" + Guid.NewGuid().ToString("N"));
    try
    {
        CopyProjectSkippingBinObj(projectDir, tempDir);
        var copyFile = Path.Combine(tempDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(copyFile)!);
        File.WriteAllText(copyFile, corrected.ReplaceLineEndings(Environment.NewLine));

        var copyBuild = await RunDotnetBuild(tempDir);
        var copyErrors = ParseErrors(copyBuild);
        var stillThere = copyErrors.Any(ce =>
            ce.Code == error.Code &&
            string.Equals(Path.GetFileName(ce.File), Path.GetFileName(error.File), StringComparison.OrdinalIgnoreCase));
        return (!stillThere, stillThere ? "Rebuild still reports it." : "");
    }
    finally
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}

static string? ExtractCsharpFile(string answer)
{
    var tagged = Regex.Match(
        answer,
        @"```(?:csharp|cs)\s*\r?\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);
    if (tagged.Success)
        return tagged.Groups[1].Value.TrimEnd();

    var generic = Regex.Match(answer, @"```\s*\r?\n(.*?)```", RegexOptions.Singleline);
    return generic.Success ? generic.Groups[1].Value.TrimEnd() : null;
}

static void CopyProjectSkippingBinObj(string sourceDir, string destDir)
{
    Directory.CreateDirectory(destDir);
    foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
    {
        if (IsBinOrObjPath(sourceDir, dir)) continue;
        Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));
    }

    foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
    {
        if (IsBinOrObjPath(sourceDir, file)) continue;
        var dest = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest, overwrite: true);
    }
}

static bool IsBinOrObjPath(string root, string path)
{
    var relative = Path.GetRelativePath(root, path);
    return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase)
               || p.Equals("obj", StringComparison.OrdinalIgnoreCase));
}

static string GetProjectDir(string target)
{
    var full = Path.GetFullPath(target);
    if (Directory.Exists(full)) return full;
    if (File.Exists(full)) return Path.GetDirectoryName(full)!;
    return full;
}

static string GetSampleName(string target)
{
    var dir = GetProjectDir(target);
    return new DirectoryInfo(dir).Name;
}

static void PrintSummary(List<VerifyResult> results)
{
    Console.WriteLine("Verify summary");
    Console.WriteLine($"{"sample",-28} {"error",-10} {"fixed",-8} {"seconds",8}");
    Console.WriteLine(new string('-', 58));
    foreach (var r in results)
    {
        Console.WriteLine(
            $"{r.Sample,-28} {r.ErrorCode,-10} {(r.Fixed ? "yes" : "no"),-8} {r.Seconds,8:F1}");
    }
}

static void AppendCsv(string model, string sample, string errorCode, bool fixedIt, double seconds)
{
    var dir = Path.Combine(Directory.GetCurrentDirectory(), "results");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "results.csv");
    var headerNeeded = !File.Exists(path) || new FileInfo(path).Length == 0;
    using var writer = new StreamWriter(path, append: true);
    if (headerNeeded)
        writer.WriteLine("timestamp,model,sample,error_code,fixed,seconds");
    writer.WriteLine(string.Join(',',
        DateTime.UtcNow.ToString("o"),
        Csv(model),
        Csv(sample),
        Csv(errorCode),
        fixedIt ? "yes" : "no",
        seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));
}

static string Csv(string value)
{
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        return '"' + value.Replace("\"", "\"\"") + '"';
    return value;
}

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
    var raw = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        throw new HttpRequestException($"Ollama returned {(int)resp.StatusCode}: {raw}");
    using var doc = JsonDocument.Parse(raw);
    return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
}

record BuildError(string File, int Line, int Col, string Code, string Message);
record VerifyResult(string Sample, string ErrorCode, bool Fixed, double Seconds);
