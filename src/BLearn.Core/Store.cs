using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace BLearn.Core;

/// <summary>
/// A learning store on disk. Layout:
/// <c>blearn.yaml</c> (project file), <c>notes/*.md</c> (concept atoms), <c>learner/</c> (journey and progress).
/// </summary>
public sealed class Store
{
    public const string ProjectFile = "blearn.yaml";
    public const string NotesDir = "notes";
    public const string LearnerDir = "learner";

    public required string RootPath { get; init; }
    public Project? Project { get; init; }
    public required IReadOnlyList<Note> Notes { get; init; }

    /// <summary>Problems found while reading files, before semantic validation.</summary>
    public required IReadOnlyList<Issue> LoadIssues { get; init; }

    public string NotesPath => Path.Combine(RootPath, NotesDir);
    public string LearnerPath => Path.Combine(RootPath, LearnerDir);

    public static Store Load(string rootPath)
    {
        rootPath = Path.GetFullPath(rootPath);
        var issues = new List<Issue>();
        var project = LoadProject(rootPath, issues);

        var notes = new List<Note>();
        var notesPath = Path.Combine(rootPath, NotesDir);
        if (!Directory.Exists(notesPath))
        {
            issues.Add(new Issue(Severity.Error, "store-layout", NotesDir, "Missing notes directory."));
        }
        else
        {
            foreach (var file in Directory.EnumerateFiles(notesPath, "*", SearchOption.AllDirectories).Order())
            {
                var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                if (!file.EndsWith(".md", StringComparison.Ordinal))
                {
                    issues.Add(new Issue(Severity.Error, "store-layout", rel, "Only .md files are allowed in notes/."));
                    continue;
                }
                if (Path.GetDirectoryName(file) != notesPath)
                {
                    issues.Add(new Issue(Severity.Error, "store-layout", rel, "Notes must sit directly in notes/, not in subfolders."));
                    continue;
                }
                var note = NoteParser.Parse(rel, File.ReadAllText(file), issues);
                if (note is not null) notes.Add(note);
            }
        }

        return new Store { RootPath = rootPath, Project = project, Notes = notes, LoadIssues = issues };
    }

    private static Project? LoadProject(string rootPath, List<Issue> issues)
    {
        var path = Path.Combine(rootPath, ProjectFile);
        if (!File.Exists(path))
        {
            issues.Add(new Issue(Severity.Error, "project-missing", ProjectFile, "Store has no project file."));
            return null;
        }
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(path)));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode map)
                throw new YamlException("Project file must be a YAML mapping.");
            string? Get(string k) =>
                map.Children.TryGetValue(new YamlScalarNode(k), out var n) ? (n as YamlScalarNode)?.Value : null;
            var name = Get("name");
            var root = Get("root");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(root))
            {
                issues.Add(new Issue(Severity.Error, "project-shape", ProjectFile, "Project file needs 'name' and 'root'."));
                return null;
            }
            return new Project(name.Trim(), root.Trim());
        }
        catch (YamlException ex)
        {
            issues.Add(new Issue(Severity.Error, "project-shape", ProjectFile, ex.Message));
            return null;
        }
    }
}
