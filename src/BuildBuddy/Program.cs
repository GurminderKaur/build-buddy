// Build Buddy
// Runs `dotnet build`, catches C# compiler errors, and asks a local model
// (via Ollama) to explain each one and suggest a fix. Fully offline.
// With --verify, a fix only counts if the patched project builds AND, when the
// sample has an expected.txt, the patched program prints that expected output.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var verify = args.Any(a => a == "--verify");
var all = args.Any(a => a == "--all");
var positional = args.Where(a => a is not ("--verify" or "--all")).ToArray();
var target = positional.Length > 0 ? positional[0] : ".";
var model = Environment.GetEnvironmentVariable("BUILDBUDDY_MODEL") ?? "gemma3:4b";
var ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434";

var targets = all ? GetSubfolders(target) : [target];
if (all && targets.Count == 0)
{
    Console.WriteLine($"No subfolders found in {target}.");
    return 1;
}

using var http = new HttpClient
{
    BaseAddress = new Uri(ollamaUrl),
    Timeout = TimeSpan.FromMinutes(5)
};

// Load the model once before timing anything, so the first error doesn't pay
// for the cold start. The load time is reported separately.
await WarmUpModel(http, model, verify);

var allResults = new List<VerifyResult>();
var anyCompilerErrors = false;

foreach (var t in targets)
{
    var (hadErrors, results) = await ProcessTarget(t, verify, http, model);
    anyCompilerErrors |= hadErrors;
    allResults.AddRange(results);
}

if (verify)
{
    PrintSummary(allResults);
    if (all)
        PrintCombinedStats(allResults);
}

return anyCompilerErrors ? 1 : 0;

// ---------- helpers ----------

static async Task<(bool HadErrors, List<VerifyResult> Results)> ProcessTarget(
    string target, bool verify, HttpClient http, string model)
{
    var sampleName = GetSampleName(target);
    var results = new List<VerifyResult>();

    Console.WriteLine($"Building {target} ...");
    var (buildOutput, _) = await RunDotnetBuild(target);
    var errors = ParseErrors(buildOutput);

    if (errors.Count == 0)
    {
        Console.WriteLine("No C# compiler errors found. Nothing to explain.\n");
        return (false, results);
    }

    Console.WriteLine($"Found {errors.Count} error(s). Asking {model} ...\n");

    foreach (var e in errors)
    {
        var snippet = ReadSnippet(e.File, e.Line, context: 4);
        var prompt = verify ? BuildVerifyPrompt(e, snippet) : BuildExplainPrompt(e, snippet);

        var sw = Stopwatch.StartNew();
        string? answer = null;
        string? ollamaError = null;
        try
        {
            answer = await AskOllama(http, model, prompt);
        }
        catch (Exception ex)
        {
            ollamaError = FriendlyOllamaError(ex);
        }
        sw.Stop();

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"{e.Code}  {Path.GetFileName(e.File)}:{e.Line}  {e.Message}");
        Console.ResetColor();

        if (ollamaError is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Could not get a reply from the local model ({model}). {ollamaError}");
            Console.WriteLine("Recording build_passes=no, fixed=no and continuing.");
            Console.ResetColor();
            if (verify)
            {
                results.Add(new VerifyResult(sampleName, e.Code, false, null, false, sw.Elapsed.TotalSeconds));
                AppendCsv(model, sampleName, e.Code, false, null, false, sw.Elapsed.TotalSeconds);
            }
        }
        else
        {
            Console.WriteLine(answer!.Trim());
            if (verify)
            {
                var outcome = await TryVerifyFix(target, e, answer);
                // Fixed = builds, and behaves as expected when the sample defines expected output.
                var fixedIt = outcome.BuildPasses && outcome.Behaves != false;
                Console.ForegroundColor = fixedIt ? ConsoleColor.Green : ConsoleColor.Yellow;
                var behavesText = YesNoNa(outcome.Behaves);
                Console.WriteLine(fixedIt
                    ? $"Verify: build_passes=yes, behaves={behavesText}; calling this fixed."
                    : $"Verify: build_passes={YesNoNa(outcome.BuildPasses)}, behaves={behavesText}; not calling this fixed. {outcome.Detail}");
                Console.ResetColor();
                results.Add(new VerifyResult(sampleName, e.Code, outcome.BuildPasses, outcome.Behaves, fixedIt, sw.Elapsed.TotalSeconds));
                AppendCsv(model, sampleName, e.Code, outcome.BuildPasses, outcome.Behaves, fixedIt, sw.Elapsed.TotalSeconds);
            }
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"({sw.Elapsed.TotalSeconds:F1}s with {model})\n");
        Console.ResetColor();
    }

    return (true, results);
}

static string FriendlyOllamaError(Exception ex)
{
    var text = ex.Message;
    if (text.Contains("out-of-memory", StringComparison.OrdinalIgnoreCase)
        || text.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
        return "The model ran out of memory.";
    if (ex is TaskCanceledException or OperationCanceledException)
        return "The request timed out.";
    if (ex is HttpRequestException)
        return "Ollama returned an error. Is it running, and is the model pulled?";
    return "Unexpected error talking to Ollama.";
}

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

static async Task<VerifyOutcome> TryVerifyFix(string target, BuildError error, string answer)
{
    var corrected = ExtractCsharpFile(answer);
    if (corrected is null)
        return new VerifyOutcome(false, null, "No csharp code block in the model reply.");

    var projectDir = GetProjectDir(target);
    var originalFull = Path.GetFullPath(error.File);
    var relative = Path.GetRelativePath(projectDir, originalFull);
    if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        return new VerifyOutcome(false, null, "Failing file is outside the target project folder.");

    var tempDir = Path.Combine(Path.GetTempPath(), "build-buddy-" + Guid.NewGuid().ToString("N"));
    try
    {
        CopyProjectSkippingBinObj(projectDir, tempDir);
        var copyFile = Path.Combine(tempDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(copyFile)!);
        File.WriteAllText(copyFile, corrected.ReplaceLineEndings(Environment.NewLine));

        var (copyBuild, exitCode) = await RunDotnetBuild(tempDir);
        var copyErrors = ParseErrors(copyBuild);
        var buildPasses = exitCode == 0 && copyErrors.Count == 0;
        if (buildPasses)
        {
            // Behaviour check: only when the sample says what it should print.
            var expectedFile = Path.Combine(projectDir, "expected.txt");
            if (!File.Exists(expectedFile))
                return new VerifyOutcome(true, null, "No expected.txt, so behaviour was not checked.");

            var expected = NormalizeOutput(File.ReadAllText(expectedFile));
            var (actualRaw, runExit, timedOut) = await RunBuiltProgram(tempDir);
            if (timedOut)
                return new VerifyOutcome(true, false, $"Program did not finish within {Limits.RunTimeout.TotalSeconds:F0}s.");
            if (runExit != 0)
                return new VerifyOutcome(true, false, $"Program exited with code {runExit}.");

            var actual = NormalizeOutput(actualRaw);
            return actual == expected
                ? new VerifyOutcome(true, true, "")
                : new VerifyOutcome(true, false, $"Output changed. Expected {Quote(expected)}, got {Quote(actual)}.");
        }

        var stillThere = copyErrors.Any(ce =>
            ce.Code == error.Code &&
            string.Equals(Path.GetFileName(ce.File), Path.GetFileName(error.File), StringComparison.OrdinalIgnoreCase));
        var detail = stillThere
            ? $"Rebuild still reports {error.Code}."
            : copyErrors.Count > 0
                ? $"Rebuild failed with {copyErrors.Count} other error(s)."
                : $"dotnet build exited {exitCode}.";
        return new VerifyOutcome(false, null, detail);
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

static List<string> GetSubfolders(string path)
{
    var full = Path.GetFullPath(path);
    if (!Directory.Exists(full)) return [];
    return Directory.GetDirectories(full)
        .Where(d =>
        {
            var name = Path.GetFileName(d);
            return !name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("obj", StringComparison.OrdinalIgnoreCase);
        })
        .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static void PrintSummary(List<VerifyResult> results)
{
    Console.WriteLine("Verify summary");
    Console.WriteLine($"{"sample",-28} {"error",-10} {"build_passes",-14} {"behaves",-9} {"fixed",-8} {"seconds",8}");
    Console.WriteLine(new string('-', 82));
    foreach (var r in results)
    {
        Console.WriteLine(
            $"{r.Sample,-28} {r.ErrorCode,-10} {YesNoNa(r.BuildPasses),-14} {YesNoNa(r.Behaves),-9} {YesNoNa(r.Fixed),-8} {r.Seconds,8:F1}");
    }
}

static void PrintCombinedStats(List<VerifyResult> results)
{
    if (results.Count == 0)
    {
        Console.WriteLine("No verify results.");
        return;
    }

    var n = results.Count;
    var builds = results.Count(r => r.BuildPasses);
    var fixedCount = results.Count(r => r.Fixed);
    var avg = results.Average(r => r.Seconds);
    Console.WriteLine();
    Console.WriteLine($"Builds: {builds} of {n}. Builds and behaves (fixed): {fixedCount} of {n} ({100.0 * fixedCount / n:F0}%). Average {avg:F1}s per error.");
}

// results.csv keeps the original 7-column format used by the Day 1-3 posts and the
// Tiger Data import. Behaviour-checked runs go to results-v2.csv with a behaves column.
static void AppendCsv(string model, string sample, string errorCode, bool buildPasses, bool? behaves, bool fixedIt, double seconds)
{
    var dir = Path.Combine(Directory.GetCurrentDirectory(), "results");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "results-v2.csv");
    var headerNeeded = !File.Exists(path) || new FileInfo(path).Length == 0;
    using var writer = new StreamWriter(path, append: true);
    if (headerNeeded)
        writer.WriteLine("timestamp,model,sample,error_code,build_passes,behaves,fixed,seconds");
    writer.WriteLine(string.Join(',',
        DateTime.UtcNow.ToString("o"),
        Csv(model),
        Csv(sample),
        Csv(errorCode),
        YesNoNa(buildPasses),
        YesNoNa(behaves),
        YesNoNa(fixedIt),
        seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));
}

static void AppendWarmupCsv(string model, double seconds, bool ok)
{
    var dir = Path.Combine(Directory.GetCurrentDirectory(), "results");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "warmup.csv");
    var headerNeeded = !File.Exists(path) || new FileInfo(path).Length == 0;
    using var writer = new StreamWriter(path, append: true);
    if (headerNeeded)
        writer.WriteLine("timestamp,model,load_seconds,ok");
    writer.WriteLine(string.Join(',',
        DateTime.UtcNow.ToString("o"),
        Csv(model),
        seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
        ok ? "yes" : "no"));
}

static async Task WarmUpModel(HttpClient http, string model, bool record)
{
    // Ollama loads a model into memory when it receives a generate request with no prompt.
    Console.Write($"Loading {model} (cold start, timed separately) ... ");
    var sw = Stopwatch.StartNew();
    var ok = true;
    try
    {
        using var resp = await http.PostAsJsonAsync("/api/generate", new { model });
        ok = resp.IsSuccessStatusCode;
    }
    catch
    {
        ok = false;
    }
    sw.Stop();
    Console.WriteLine(ok
        ? $"{sw.Elapsed.TotalSeconds:F1}s\n"
        : $"failed after {sw.Elapsed.TotalSeconds:F1}s; continuing (the first error may include load time).\n");
    if (record)
        AppendWarmupCsv(model, sw.Elapsed.TotalSeconds, ok);
}

static async Task<(string Output, int ExitCode, bool TimedOut)> RunBuiltProgram(string projectDir)
{
    var psi = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        UseShellExecute = false
    };
    foreach (var a in new[] { "run", "--project", projectDir, "--no-build" })
        psi.ArgumentList.Add(a);

    using var p = Process.Start(psi)!;
    p.StandardInput.Close(); // a program waiting for input gets end-of-input instead of hanging
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    using var cts = new CancellationTokenSource(Limits.RunTimeout);
    try
    {
        await p.WaitForExitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
        try { await p.WaitForExitAsync(); } catch { /* best effort: let the OS clean it up */ }
        return ("", -1, true);
    }
    _ = await stderr;
    return (await stdout, p.ExitCode, false);
}

// Compare output by content: same lines, ignoring line-ending style,
// trailing spaces, and trailing blank lines.
static string NormalizeOutput(string text) =>
    string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()))
        .TrimEnd('\n');

static string Quote(string text)
{
    var oneLine = text.Replace("\n", "\\n");
    return '"' + (oneLine.Length > 60 ? oneLine[..60] + "..." : oneLine) + '"';
}

static string YesNoNa(bool? value) => value switch { true => "yes", false => "no", null => "n/a" };

static string Csv(string value)
{
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        return '"' + value.Replace("\"", "\"\"") + '"';
    return value;
}

static async Task<(string Output, int ExitCode)> RunDotnetBuild(string target)
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
    return (await stdout + await stderr, p.ExitCode);
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
// Behaves: true/false when the sample has expected.txt and the build passed; null when not checked.
record VerifyOutcome(bool BuildPasses, bool? Behaves, string Detail);
record VerifyResult(string Sample, string ErrorCode, bool BuildPasses, bool? Behaves, bool Fixed, double Seconds);

static class Limits
{
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);
}
