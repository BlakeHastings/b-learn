using System.Text.RegularExpressions;

namespace BLearn.Core;

/// <summary>
/// Deterministic invariants for a store. Every rule here is a hard guarantee that agents and
/// tools may rely on; prose instructions should never be the only thing enforcing one.
/// </summary>
public static partial class Validator
{
    public const int GlossMaxLength = 200;
    public static readonly string[] CompleteSections = ["Summary", "Detail"];

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdRegex();

    public static List<Issue> Validate(Store store)
    {
        var issues = new List<Issue>(store.LoadIssues);
        void Add(Severity s, string code, string file, string msg) => issues.Add(new Issue(s, code, file, msg));

        var notes = store.Notes;

        // Identity: ids are unique, match the filename, and no two notes share a name.
        var byId = new Dictionary<string, Note>();
        var names = new Dictionary<string, Note>();
        foreach (var n in notes)
        {
            if (!IdRegex().IsMatch(n.Id))
                Add(Severity.Error, "id-format", n.FilePath, $"Id '{n.Id}' must be lowercase kebab-case.");
            if (Path.GetFileNameWithoutExtension(n.FilePath) != n.Id)
                Add(Severity.Error, "id-filename", n.FilePath, $"Filename must be '{n.Id}.md'.");
            if (!byId.TryAdd(n.Id, n))
                Add(Severity.Error, "id-duplicate", n.FilePath, $"Id '{n.Id}' is already used by {byId[n.Id].FilePath}.");

            foreach (var name in n.Aliases.Prepend(n.Title).Prepend(n.Id))
            {
                var key = NormalizeName(name);
                if (names.TryGetValue(key, out var other) && other.Id != n.Id)
                    Add(Severity.Error, "name-duplicate", n.FilePath, $"Name '{name}' collides with note '{other.Id}'. Reuse that note or rename.");
                else
                    names[key] = n;
            }
        }

        foreach (var n in notes)
        {
            // Field values.
            if (!NoteStatus.All.Contains(n.Status))
                Add(Severity.Error, "status-invalid", n.FilePath, $"Status must be one of: {string.Join(", ", NoteStatus.All)}.");
            if (n.Gloss.Contains('\n'))
                Add(Severity.Error, "gloss-format", n.FilePath, "Gloss must be a single line.");
            if (n.Gloss.Length > GlossMaxLength)
                Add(Severity.Error, "gloss-format", n.FilePath, $"Gloss is {n.Gloss.Length} chars; max is {GlossMaxLength}.");
            if (n.Gloss.Contains("[["))
                Add(Severity.Error, "gloss-format", n.FilePath, "Gloss must not contain wikilinks.");

            // Body shape.
            if (n.IsStub)
            {
                if (n.Preamble.Length > 0 || n.Sections.Count > 0)
                    Add(Severity.Error, "stub-body", n.FilePath, "A stub has a gloss only; its body must be empty.");
                if (n.Edges.Count > 0)
                    Add(Severity.Error, "stub-edges", n.FilePath, "A stub must not declare edges; expand it to complete first.");
            }
            else if (n.Status == NoteStatus.Complete)
            {
                if (n.Preamble.Length > 0)
                    Add(Severity.Error, "body-shape", n.FilePath, "Text before the first '## ' heading is not allowed.");
                var headings = n.Sections.Select(s => s.Heading).ToArray();
                if (!headings.SequenceEqual(CompleteSections))
                    Add(Severity.Error, "body-shape", n.FilePath, $"Sections must be exactly: {string.Join(", ", CompleteSections.Select(h => "## " + h))} (found: {string.Join(", ", headings.Select(h => "## " + h))}).");
                foreach (var s in n.Sections.Where(s => s.Content.Length == 0))
                    Add(Severity.Error, "body-shape", n.FilePath, $"Section '{s.Heading}' is empty.");
            }

            // Edges.
            var seenTargets = new HashSet<string>();
            foreach (var e in n.Edges)
            {
                if (!EdgeTypes.All.ContainsKey(e.Type))
                    Add(Severity.Error, "edge-type", n.FilePath, $"Unknown edge type '{e.Type}'. Allowed: {string.Join(", ", EdgeTypes.All.Keys)}.");
                if (e.Target == n.Id)
                    Add(Severity.Error, "edge-self", n.FilePath, $"Edge '{e.Type}' points at the note itself.");
                else if (!byId.ContainsKey(e.Target))
                    Add(Severity.Error, "edge-dangling", n.FilePath, $"Edge '{e.Type}' targets missing note '{e.Target}'. Create it (a stub is fine).");
                if (!seenTargets.Add(e.Target))
                    Add(Severity.Error, "edge-multiple", n.FilePath, $"More than one edge to '{e.Target}'. A pair of notes has exactly one relationship.");
            }
        }

        // A pair of notes has one relationship, stored on one side only.
        foreach (var n in notes)
            foreach (var e in n.Edges)
                if (byId.TryGetValue(e.Target, out var t) && string.CompareOrdinal(n.Id, t.Id) < 0 && t.Edges.Any(b => b.Target == n.Id))
                    Add(Severity.Error, "edge-both-sides", n.FilePath, $"'{n.Id}' and '{t.Id}' each declare an edge to the other. Keep one.");

        // Downward edges form a strict hierarchy: going down can never bring you back.
        foreach (var cycle in FindDownCycles(notes, byId))
            Add(Severity.Error, "edge-cycle", byId[cycle[0]].FilePath, $"Downward cycle: {string.Join(" -> ", cycle)}.");

        // Inline links need a declared relationship so the UI always knows their direction.
        var graph = new Graph(notes);
        foreach (var n in notes)
        {
            var text = string.Join('\n', n.Sections.Select(s => s.Content).Prepend(n.Preamble));
            foreach (var link in Wikilinks.Scan(text))
            {
                if (!byId.ContainsKey(link.Target))
                    Add(Severity.Error, "link-dangling", n.FilePath, $"Link [[{link.Target}]] has no note. Links use note ids, not titles.");
                else if (link.Target == n.Id)
                    Add(Severity.Error, "link-self", n.FilePath, $"Note links to itself.");
                else if (graph.Relation(n.Id, link.Target) is null)
                    Add(Severity.Error, "link-no-edge", n.FilePath, $"Link [[{link.Target}]] has no edge between '{n.Id}' and '{link.Target}'. Declare one.");
            }
        }

        // Project root and reachability.
        if (store.Project is { } project)
        {
            if (!byId.ContainsKey(project.Root))
            {
                Add(Severity.Error, "project-root", Store.ProjectFile, $"Root note '{project.Root}' does not exist.");
            }
            else
            {
                var reached = Reachable(project.Root, graph);
                foreach (var n in notes.Where(n => !reached.Contains(n.Id)))
                    Add(Severity.Warning, "note-orphan", n.FilePath, $"Not connected to root '{project.Root}'.");
            }
        }

        return issues;
    }

    public static string NormalizeName(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant(), @"[\s_-]+", " ");

    private static List<List<string>> FindDownCycles(IReadOnlyList<Note> notes, Dictionary<string, Note> byId)
    {
        var down = byId.Values.ToDictionary(
            n => n.Id,
            n => n.Edges
                .Where(e => EdgeTypes.All.TryGetValue(e.Type, out var t) && t.Direction == Direction.Down && byId.ContainsKey(e.Target))
                .Select(e => e.Target)
                .ToList());

        var state = new Dictionary<string, int>(); // 1 = on stack, 2 = done
        var stack = new List<string>();
        var cycles = new List<List<string>>();

        void Visit(string id)
        {
            state[id] = 1;
            stack.Add(id);
            foreach (var next in down.GetValueOrDefault(id, []))
            {
                if (state.GetValueOrDefault(next) == 1)
                    cycles.Add([.. stack.Skip(stack.IndexOf(next)), next]);
                else if (state.GetValueOrDefault(next) == 0)
                    Visit(next);
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }

        foreach (var id in down.Keys.Order())
            if (state.GetValueOrDefault(id) == 0) Visit(id);
        return cycles;
    }

    private static HashSet<string> Reachable(string root, Graph graph)
    {
        var seen = new HashSet<string> { root };
        var queue = new Queue<string>([root]);
        while (queue.Count > 0)
            foreach (var n in graph.NeighborsOf(queue.Dequeue()))
                if (seen.Add(n.Note.Id)) queue.Enqueue(n.Note.Id);
        return seen;
    }
}
