using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BLearn.Infrastructure.Agents;

/// <summary>The outcome of one agent run on one task.</summary>
public sealed record AgentRunResult(bool Succeeded, string? Error);

/// <summary>Runs the user's agent harness on one prompt, reporting what it is doing as it goes.</summary>
public interface IAgentProcess
{
    /// <param name="environment">Extra environment variables for the agent (for example BLEARN_STORE and BLEARN_SESSION).</param>
    Task<AgentRunResult> RunAsync(string prompt, string workDirectory, IReadOnlyDictionary<string, string> environment, Action<string> onActivity, CancellationToken ct);
}

/// <summary>An agent harness b-learn can start itself.</summary>
public sealed record HarnessInfo(string Id, string Name, string? ExecutablePath)
{
    public bool Available => ExecutablePath is not null;
}

public static class Harnesses
{
    public const string ClaudeCode = "claude-code";

    /// <summary>Harnesses b-learn knows how to start, with where each is installed (null when not found).</summary>
    public static IReadOnlyList<HarnessInfo> Detect() => [new(ClaudeCode, "Claude Code", FindOnPath("claude"))];

    public static IAgentProcess Create(string id, string blearnExe) => id switch
    {
        ClaudeCode => new ClaudeCodeProcess(FindOnPath("claude") ?? throw new InvalidOperationException("Claude Code (claude) is not installed or not on PATH."), blearnExe),
        _ => throw new ArgumentException($"Unknown harness '{id}'."),
    };

    internal static string? FindOnPath(string name)
    {
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        foreach (var dir in dirs)
            foreach (var ext in exts)
            {
                var p = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}

/// <summary>
/// Claude Code in headless mode. Permissions are an allowlist that never prompts: blearn commands,
/// reading the web, and files in its own work folder. Everything else is refused.
/// </summary>
internal sealed class ClaudeCodeProcess(string claudePath, string blearnExe) : IAgentProcess
{
    public async Task<AgentRunResult> RunAsync(string prompt, string workDirectory, IReadOnlyDictionary<string, string> environment, Action<string> onActivity, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(claudePath)
        {
            WorkingDirectory = workDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // blearn is on PATH and the store and session are in the environment, so the natural command is
        // simply "blearn <resource> <verb> ...", which is exactly what the allowlist permits.
        foreach (var (k, v) in environment) psi.Environment[k] = v;
        var blearnDir = Path.GetDirectoryName(Path.GetFullPath(blearnExe))!;
        psi.Environment["PATH"] = blearnDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        foreach (var arg in new[]
        {
            "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "dontAsk",
            "--allowedTools", "Bash(blearn *)", "PowerShell(blearn *)", "WebFetch", "WebSearch", "Read", "Write", "Edit",
        }) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Claude Code.");
        using var _ = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(ct);

        string? resultError = null;
        var succeeded = false;
        await using var transcript = new StreamWriter(Path.Combine(workDirectory, "transcript.jsonl"));
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            await transcript.WriteLineAsync(line);
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "assistant" && root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                {
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("type", out var pt) && pt.GetString() == "tool_use" && Describe(part) is { } activity)
                            onActivity(activity);
                }
                else if (type == "result")
                {
                    succeeded = !(root.TryGetProperty("is_error", out var e) && e.GetBoolean());
                    if (!succeeded) resultError = root.TryGetProperty("result", out var res) ? res.ToString() : "The agent reported an error.";
                }
            }
            catch (JsonException) { }
        }
        await process.WaitForExitAsync(ct);
        var err = await stderr;
        if (process.ExitCode != 0 && resultError is null)
            resultError = $"Claude Code exited with code {process.ExitCode}. {Tail(err)}";
        return new AgentRunResult(succeeded && process.ExitCode == 0, resultError);
    }

    /// <summary>A short, plain description of a tool call for the status line.</summary>
    internal static string? Describe(JsonElement toolUse)
    {
        var name = toolUse.GetProperty("name").GetString();
        var input = toolUse.TryGetProperty("input", out var i) ? i : default;
        string? Str(string p) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(p, out var v) ? v.GetString() : null;
        switch (name)
        {
            case "WebFetch":
                return Uri.TryCreate(Str("url"), UriKind.Absolute, out var u) ? $"Reading {u.Host}" : "Reading a web page";
            case "WebSearch":
                return $"Searching the web for \"{Str("query")}\"";
            case "Bash" or "PowerShell":
                var command = Str("command") ?? "";
                var words = command.Replace("\"", "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .SkipWhile(w => w is not "blearn" && !w.EndsWith("blearn.exe") && !w.EndsWith("/blearn")).Skip(1)
                    .Where(w => !w.StartsWith("--") && !w.Contains(".blearn")).Take(2).ToList();
                return words.Count == 2 ? Plain(words[0], words[1]) : "Working with the store";
            case "Write" or "Edit":
                return "Writing notes";
            default:
                return null;
        }
    }

    private static string Plain(string resource, string verb) => (resource, verb) switch
    {
        ("page", "get") => "Reading a source",
        ("evidence", "post") => "Saving evidence",
        ("facts", "put") => "Writing facts",
        ("teaching", "put") => "Writing teaching",
        ("concept", "post") => "Adding a concept",
        ("concept", "patch") => "Updating a concept",
        ("edge", "post") or ("edge-evidence", "post") => "Linking concepts",
        ("reply", "post") => "Sending the answer",
        ("recall-question", "put") => "Writing quiz questions",
        ("check", "put") => "Saving the check",
        ("concept", "get") or ("concept", "list") or ("neighbor", "list") or ("conversation", "get") => "Reading the graph",
        _ => $"Running {resource} {verb}",
    };

    private static string Tail(string s) => s.Length <= 400 ? s.Trim() : s[^400..].Trim();
}
