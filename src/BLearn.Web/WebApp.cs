using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using BLearn.Application;
using BLearn.Domain;
using BLearn.Infrastructure.Fetching;
using BLearn.Infrastructure.Sqlite;
using Markdig;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BLearn.Web;

/// <param name="StorePath">Store to open at start, or null to show the store picker.</param>
/// <param name="Exe">How agents should invoke the CLI, shown in the connection panel.</param>
public sealed record WebAppOptions(string? StorePath, int Port, bool OpenBrowser, string Exe);

/// <summary>The local web UI: a second adapter over the same application layer as the CLI.</summary>
public static class WebApp
{
    public static async Task RunAsync(WebAppOptions options)
    {
        var clock = new SystemClock();
        var provider = new SqliteStoreProvider(clock);
        var fetcher = new SourceFetcher(SourceFetcher.CreateClient());
        var session = new StoreSession(provider, clock, fetcher);
        if (options.StorePath is not null) session.Open(options.StorePath);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://localhost:{options.Port}");
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var app = builder.Build();
        var markdown = new MarkdownPipelineBuilder().UsePipeTables().Build();

        app.MapGet("/", () => Results.Stream(Resource("BLearn.Web.index.html"), "text/html; charset=utf-8"));

        app.MapGet("/api/state", () => Results.Json(new
        {
            store = session.Current?.Queries.GetInfo(),
            recent = RecentStores.Load(),
            exe = options.Exe,
            edgeTypes = EdgeTypes.List.Select(t => new { t.Name, direction = t.Direction.ToString().ToLowerInvariant(), t.InverseLabel }),
        }));

        app.MapPost("/api/store/open", (OpenStoreRequest r) => Guard(() =>
        {
            session.Open(r.Path);
            return Results.Json(session.Current!.Queries.GetInfo());
        }));

        app.MapPost("/api/store/create", (CreateStoreRequest r) => Guard(() =>
        {
            var path = r.Path.EndsWith(".blearn", StringComparison.OrdinalIgnoreCase) ? r.Path : r.Path + ".blearn";
            var result = session.Create(path, r.Name, r.RootTitle, r.RootGloss, r.Private);
            return result.Accepted ? Results.Json(session.Current!.Queries.GetInfo()) : Results.Json(result, statusCode: 422);
        }));

        app.MapPost("/api/store/close", () => { session.Close(); return Results.NoContent(); });

        app.MapGet("/api/concepts/{id}", (string id) => WithStore(session, store =>
        {
            var c = store.Queries.GetConcept(id);
            if (c is null) return Results.NotFound();
            var titles = store.Queries.ListConcepts().ToDictionary(x => x.Id, x => (x.Title, x.Status));
            string Html(string? md) => Render(md, titles, markdown);
            return Results.Json(new
            {
                concept = c,
                summaryHtml = Html(c.Summary),
                explanationHtml = Html(c.Explanation),
                facts = c.Facts.Select(f => new { f.Index, html = InlineHtml(f.Text, titles, markdown), f.Verified, f.Evidence }),
            });
        }));

        app.MapGet("/api/relation", (string from, string to) => WithStore(session, store =>
            Results.Json(store.Queries.Relation(from, to))));

        app.MapGet("/api/graph/{id}", (string id, int? hops) => WithStore(session, store =>
            Results.Json(store.Queries.GraphAround(id, Math.Clamp(hops ?? 2, 1, 4)))));

        app.MapGet("/api/search", (string q) => WithStore(session, store => Results.Json(store.Queries.Search(q))));

        app.MapPost("/api/concepts/{id}/requests", (string id, LearnerRequest r) => WithStore(session, store =>
        {
            var cmd = session.Commands!;
            var result = r.Kind switch
            {
                "expand" => cmd.RequestExpansion(id),
                "teach" => cmd.RequestTeaching(id, r.FactIndex),
                "ask" => cmd.AskQuestion(id, r.Text ?? ""),
                "recall" => cmd.RequestRecall(id),
                _ => CommandResult.Rejected("kind-invalid", "Kind must be expand, teach, ask or recall."),
            };
            return Results.Json(result, statusCode: result.Accepted ? 200 : 422);
        }));

        app.MapPost("/api/learner/visit", (VisitRequest r) => WithStore(session, _ =>
        {
            var result = session.Commands!.RecordVisit(r.ConceptId, r.From, r.Direction);
            return Results.Json(result, statusCode: result.Accepted ? 200 : 422);
        }));

        app.MapPost("/api/learner/known", (KnownRequest r) => WithStore(session, _ =>
        {
            var result = r.Known ? session.Commands!.MarkKnown(r.ConceptId) : session.Commands!.UnmarkKnown(r.ConceptId);
            return Results.Json(result, statusCode: result.Accepted ? 200 : 422);
        }));

        app.MapGet("/api/learner", () => WithStore(session, store => Results.Json(new { known = store.Queries.Known(), journey = store.Queries.Journey(50) })));

        app.MapGet("/api/queue", () => WithStore(session, store => Results.Json(new
        {
            open = store.Queue.List(),
            dead = store.Queue.DeadLetters(),
        })));

        app.MapGet("/api/brief", () => WithStore(session, store =>
            Results.Text(AgentBrief.Render(store.Queries.GetInfo(), options.Exe), "text/plain; charset=utf-8")));

        // Live updates: the store file is the bus. Poll the event log and forward new events as server-sent events.
        app.MapGet("/api/events/stream", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var store = session.Current;
            var after = store?.Events.LatestSeq() ?? 0;
            await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                try { await Task.Delay(500, ctx.RequestAborted); } catch (TaskCanceledException) { break; }
                if (!ReferenceEquals(store, session.Current))
                {
                    store = session.Current;
                    after = store?.Events.LatestSeq() ?? 0;
                    await ctx.Response.WriteAsync("event: store\ndata: {}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    continue;
                }
                if (store is null) continue;
                var events = store.Events.Read(after, null, 200);
                if (events.Count == 0) continue;
                after = events[^1].Seq;
                var data = JsonSerializer.Serialize(events.Select(e => new { e.Seq, e.Type, e.Stream }), Json.Options);
                await ctx.Response.WriteAsync($"event: changes\ndata: {data}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        });

        var url = $"http://localhost:{options.Port}";
        Console.WriteLine($"b-learn UI at {url}" + (session.Current is null ? " (choose a store)" : $" ({session.Current.Path})"));
        if (options.OpenBrowser) OpenBrowser(url);
        await app.RunAsync();
    }

    private static IResult WithStore(StoreSession session, Func<IStore, IResult> act) =>
        session.Current is { } store ? act(store) : Results.Json(new { error = "No store is open." }, statusCode: 409);

    private static IResult Guard(Func<IResult> act)
    {
        try { return act(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
    }

    private static string Render(string? md, IReadOnlyDictionary<string, (string Title, string Status)> titles, MarkdownPipeline pipeline) =>
        md is null ? "" : Markdown.ToHtml(Wikilinks.Replace(md, link => LinkHtml(link, titles)), pipeline);

    private static string InlineHtml(string md, IReadOnlyDictionary<string, (string Title, string Status)> titles, MarkdownPipeline pipeline)
    {
        var html = Render(md, titles, pipeline).Trim();
        return html.StartsWith("<p>") && html.EndsWith("</p>") ? html[3..^4] : html;
    }

    private static string LinkHtml(WikiLink link, IReadOnlyDictionary<string, (string Title, string Status)> titles)
    {
        var label = WebUtility.HtmlEncode(link.Label ?? (titles.TryGetValue(link.Target, out var t) ? t.Title : link.Target));
        if (!titles.TryGetValue(link.Target, out var target)) return $"<span class=\"wl broken\">{label}</span>";
        var stub = target.Status == "stub" ? " stub" : "";
        return $"<a class=\"wl{stub}\" data-id=\"{link.Target}\" href=\"#/c/{link.Target}\">{label}</a>";
    }

    private static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing resource {name}.");

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Open {url} in your browser.");
        }
    }
}

public sealed record OpenStoreRequest(string Path);
public sealed record CreateStoreRequest(string Path, string Name, string RootTitle, string RootGloss, bool Private);
public sealed record LearnerRequest(string Kind, int? FactIndex, string? Text);
public sealed record VisitRequest(string ConceptId, string? From, string? Direction);
public sealed record KnownRequest(string ConceptId, bool Known);

/// <summary>The store the UI currently has open. Switching stores is serialized.</summary>
internal sealed class StoreSession(IStoreProvider provider, IClock clock, ISourceFetcher fetcher)
{
    private readonly Lock _lock = new();

    public IStore? Current { get; private set; }
    public BLearnCommands? Commands { get; private set; }

    public void Open(string path)
    {
        lock (_lock)
        {
            var store = provider.Open(path);
            Swap(store);
            RecentStores.Touch(store.Path, store.Queries.GetInfo().Name);
        }
    }

    public CommandResult Create(string path, string name, string rootTitle, string rootGloss, bool isPrivate)
    {
        lock (_lock)
        {
            var store = provider.Create(path);
            var result = new BLearnCommands(store, clock, fetcher).InitializeStore(name, rootTitle, rootGloss, isPrivate);
            if (!result.Accepted)
            {
                store.Dispose();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                File.Delete(store.Path);
                return result;
            }
            Swap(store);
            RecentStores.Touch(store.Path, name);
            return result;
        }
    }

    public void Close()
    {
        lock (_lock) Swap(null);
    }

    private void Swap(IStore? store)
    {
        Current?.Dispose();
        Current = store;
        Commands = store is null ? null : new BLearnCommands(store, clock, fetcher);
    }
}
