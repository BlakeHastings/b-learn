using System.Net;
using System.Reflection;
using System.Text.Json;
using BLearn.Core;
using Markdig;

return args switch
{
    ["validate", var path] => Validate(path),
    ["serve", var path] => await Serve(path, 5170),
    ["serve", var path, "--port", var port] when int.TryParse(port, out var p) => await Serve(path, p),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          blearn validate <store>             check every invariant; exit 1 on errors
          blearn serve <store> [--port N]     run the local learning UI (default port 5170)
        """);
    return 2;
}

static int Validate(string path)
{
    var store = Store.Load(path);
    var issues = Validator.Validate(store);
    foreach (var issue in issues.OrderBy(i => i.Severity).ThenBy(i => i.File))
        Console.WriteLine(issue);
    var errors = issues.Count(i => i.Severity == Severity.Error);
    var warnings = issues.Count - errors;
    Console.WriteLine($"{store.Notes.Count} notes, {errors} errors, {warnings} warnings");
    return errors > 0 ? 1 : 0;
}

static async Task<int> Serve(string path, int port)
{
    var storePath = Path.GetFullPath(path);
    if (Validate(storePath) != 0)
        Console.WriteLine("Serving anyway; the UI shows the error count.");

    var builder = WebApplication.CreateBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseUrls($"http://localhost:{port}");
    var app = builder.Build();

    var markdown = new MarkdownPipelineBuilder().UsePipeTables().Build();
    var journeyLock = new SemaphoreSlim(1);

    // The store is re-read per request so edits on disk show up on refresh. Fine at prototype scale.
    (Store Store, Graph Graph) Load()
    {
        var s = Store.Load(storePath);
        return (s, new Graph(s.Notes));
    }

    string Render(string? md, Graph graph) => md is null ? "" : Markdown.ToHtml(Wikilinks.Replace(md, link =>
    {
        var target = graph.Find(link.Target);
        var label = WebUtility.HtmlEncode(link.Label ?? target?.Title ?? link.Target);
        return target is null
            ? $"<span class=\"wl broken\">{label}</span>"
            : $"<a class=\"wl{(target.IsStub ? " stub" : "")}\" data-id=\"{target.Id}\" href=\"#/note/{target.Id}\">{label}</a>";
    }), markdown);

    static string Dir(Direction d) => d.ToString().ToLowerInvariant();

    app.MapGet("/", () =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BLearn.Cli.index.html")!;
        return Results.Stream(new MemoryStream(ReadAll(stream)), "text/html; charset=utf-8");
    });

    app.MapGet("/api/project", () =>
    {
        var (store, _) = Load();
        var issues = Validator.Validate(store);
        return Results.Json(new
        {
            name = store.Project?.Name ?? Path.GetFileName(storePath),
            root = store.Project?.Root,
            storePath,
            noteCount = store.Notes.Count,
            errors = issues.Count(i => i.Severity == Severity.Error),
            warnings = issues.Count(i => i.Severity == Severity.Warning),
        });
    });

    app.MapGet("/api/notes/{id}", (string id, string? from) =>
    {
        var (_, graph) = Load();
        var note = graph.Find(id);
        if (note is null) return Results.NotFound();
        var relation = from is null ? null : graph.Relation(from, id);
        return Results.Json(new
        {
            note.Id,
            note.Title,
            note.Gloss,
            note.Status,
            note.Aliases,
            summaryHtml = Render(note.SectionText("Summary"), graph),
            detailHtml = Render(note.SectionText("Detail"), graph),
            relation = relation is null ? null : new { direction = Dir(relation.Direction), relation.Label },
            neighbors = graph.NeighborsOf(id).Select(n => new
            {
                n.Note.Id,
                n.Note.Title,
                n.Note.Gloss,
                n.Note.Status,
                direction = Dir(n.Direction),
                n.Label,
            }),
        });
    });

    // Journey events are appended as JSON lines: the learner's trail through the graph, kept in the store.
    app.MapPost("/api/journey", async (JsonElement evt) =>
    {
        var dir = Path.Combine(storePath, Store.LearnerDir);
        Directory.CreateDirectory(dir);
        var line = JsonSerializer.Serialize(new { at = DateTimeOffset.Now, evt }) + "\n";
        await journeyLock.WaitAsync();
        try { await File.AppendAllTextAsync(Path.Combine(dir, "journey.jsonl"), line); }
        finally { journeyLock.Release(); }
        return Results.NoContent();
    });

    Console.WriteLine($"b-learn serving {storePath} at http://localhost:{port}");
    await app.RunAsync();
    return 0;
}

static byte[] ReadAll(Stream s)
{
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
}
