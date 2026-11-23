namespace BLearn.Cli;

/// <summary>Thrown for malformed command lines; reported with usage and exit code 2.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>Minimal parser: positionals plus <c>--name value</c> options and boolean <c>--flag</c>s.</summary>
public sealed class CliArgs
{
    /// <summary>Options that never take a value.</summary>
    private static readonly HashSet<string> Booleans = ["wait", "private", "all", "no-open", "help", "release", "recent", "waiting"];

    private readonly Dictionary<string, List<string>> _options = new();
    private readonly HashSet<string> _flags = [];
    private readonly HashSet<string> _used = [];

    public List<string> Positional { get; } = [];

    public CliArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith("--") && a.Length > 2)
            {
                var name = a[2..];
                string? inline = null;
                var eq = name.IndexOf('=');
                if (eq > 0) (name, inline) = (name[..eq], name[(eq + 1)..]);
                if (Booleans.Contains(name) && inline is null) { _flags.Add(name); continue; }
                // An option followed by another option (or nothing) is a flag; EnsureAllUsed rejects it if the command expected a value or does not know it.
                if (inline is null && (i + 1 >= args.Count || IsOption(args[i + 1]))) { _flags.Add(name); continue; }
                var value = inline ?? args[++i];
                if (!_options.TryGetValue(name, out var list)) _options[name] = list = [];
                list.Add(value);
            }
            else
            {
                Positional.Add(a);
            }
        }
    }

    private static bool IsOption(string a) => a.StartsWith("--") && a.Length > 2;

    public string At(int index, string what) =>
        index < Positional.Count ? Positional[index] : throw new UsageException($"Missing {what}.");

    public string? AtOrNull(int index) => index < Positional.Count ? Positional[index] : null;

    public bool Flag(string name)
    {
        _used.Add(name);
        return _flags.Contains(name);
    }

    public string? Opt(string name)
    {
        _used.Add(name);
        if (_flags.Contains(name)) throw new UsageException($"--{name} needs a value.");
        return _options.TryGetValue(name, out var v) ? v[^1] : null;
    }

    public string Req(string name) => Opt(name) ?? throw new UsageException($"Missing --{name}.");

    public IReadOnlyList<string> All(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var v) ? v : [];
    }

    /// <summary>Comma-separated and repeated values, for example <c>--evidence a,b --evidence c</c>.</summary>
    public IReadOnlyList<string> List(string name) =>
        All(name).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();

    public int? Int(string name) =>
        Opt(name) is { } s ? int.TryParse(s, out var i) ? i : throw new UsageException($"--{name} must be a whole number.") : null;

    /// <summary>Reads text from <c>--name</c> or <c>--name-file</c> (use "-" for stdin).</summary>
    public string? Text(string name)
    {
        if (Opt(name) is { } inline) return inline;
        if (Opt(name + "-file") is not { } path) return null;
        return path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path);
    }

    /// <summary>Fails on options the command did not read, so typos do not silently do nothing.</summary>
    public void EnsureAllUsed()
    {
        var unknown = _options.Keys.Concat(_flags).Where(k => !_used.Contains(k) && k is not ("store" or "session")).ToList();
        if (unknown.Count > 0) throw new UsageException($"Unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}.");
    }
}
