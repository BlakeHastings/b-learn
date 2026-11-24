using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using BLearn.Application;
using BLearn.Domain;
using BLearn.Infrastructure.Fetching;
using BLearn.Infrastructure.Markdown;
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
        // The command agents should run: this executable's full path when it is blearn, so pasted instructions work without PATH changes.
        var exe = AgentExe(options.Exe);
        var session = new StoreSession(provider, clock, fetcher);
        if (options.StorePath is not null) session.Open(options.StorePath);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://localhost:{options.Port}");
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var app = builder.Build();
        var markdown = new MarkdownPipelineBuilder().UsePipeTables().Build();

        app.MapGet("/", () => Results.Stream(Resource("BLearn.Web.index.html"), "text/html; charset=utf-8"));

        // Routes mirror the CLI: resources with HTTP verbs. Commands answer 200 with their events, or 422 with the broken rule.

        // ----- session: which store this UI process has open -----
        app.MapGet("/api/session", () => Results.Json(new
        {
            store = session.Current?.Queries.GetInfo(),
            recent = RecentStores.Load(),
            version = BuildInfo.Version,
            defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "b-learn"),
            defaultWebScope = StoreSession.DefaultWebScope,
            exe,
            edgeTypes = EdgeTypes.List.Select(t => new { t.Name, direction = t.Direction.ToString().ToLowerInvariant(), t.InverseLabel }),
        }));
        app.MapPost("/api/session", (OpenStoreRequest r) => Guard(() =>
        {
            session.Open(r.Path);
            return Results.Json(session.Current!.Queries.GetInfo());
        }));
        app.MapDelete("/api/session", () => { session.Close(); return Results.NoContent(); });

        // ----- store and sources -----
        app.MapPost("/api/stores", (CreateStoreRequest r) => Guard(() =>
        {
            var path = r.Path.EndsWith(".blearn", StringComparison.OrdinalIgnoreCase) ? r.Path : r.Path + ".blearn";
            var result = session.Create(path, r.Name, r.RootTitle, r.RootGloss, r.Private, r.WebSource ?? true);
            return result.Accepted ? Results.Json(session.Current!.Queries.GetInfo()) : Results.Json(result, statusCode: 422);
        }));
        app.MapGet("/api/store", () => WithStore(session, s => Results.Json(s.Queries.GetInfo())));
        app.MapPatch("/api/store", (StorePatch r) => Command(session, c => c.PatchStore(r.Private)));
        app.MapGet("/api/sources", () => WithStore(session, s => Results.Json(s.Queries.GetInfo().Sources)));
        app.MapPost("/api/sources", (SourceRequest r) => Command(session, c => c.PostSource(r.Name, r.System, r.Scope, r.MinTrust ?? "captured")));
        app.MapPost("/api/imports", (FolderRequest r) => WithStore(session, _ => Guard(() =>
        {
            var (notes, problems) = LegacyMarkdownReader.ReadFolder(r.Folder);
            if (problems.Count > 0) return Results.Json(new { accepted = false, violation = "parse-failed", problems }, statusCode: 422);
            return CommandResultJson(session.Commands!.PostImport(notes));
        })));
        app.MapPost("/api/exports", (FolderRequest r) => WithStore(session, s => Guard(() =>
            Results.Json(new { exported = MarkdownExporter.Export(s.Queries, r.Folder), folder = Path.GetFullPath(r.Folder) }))));

        // ----- concepts -----
        app.MapGet("/api/concepts", (string? status, string? search, int? limit) => WithStore(session, s =>
        {
            if (search is null) return Results.Json(s.Queries.ListConcepts(status));
            var found = s.Queries.Search(search, limit ?? 20);
            return Results.Json(status is null ? found : found.Where(c => c.Status == status));
        }));
        app.MapPost("/api/concepts", (ConceptInput r) => Command(session, c => c.PostConcept(r)));
        app.MapGet("/api/concepts/{id}", (string id) => WithStore(session, store =>
        {
            var c = store.Queries.GetConcept(id);
            if (c is null) return Results.NotFound();
            var titles = store.Queries.ListConcepts().ToDictionary(x => x.Id, x => (x.Title, x.Status));
            // Mentions of related concepts become links, so the text itself is the way to dive in.
            var neighborIds = c.Neighbors.Select(n => n.Id).ToHashSet();
            var names = store.Queries.ConceptNames().Where(n => neighborIds.Contains(n.Id)).ToList();
            string Html(string? md) => Render(md is null ? null : Wikilinks.AutoLink(md, names), titles, markdown);
            return Results.Json(new
            {
                concept = c,
                glossHtml = InlineHtml(Wikilinks.AutoLink(c.Gloss, names), titles, markdown),
                summaryHtml = Html(c.Summary),
                explanationHtml = Html(c.Explanation),
                facts = c.Facts.Select(f => new { f.Index, html = InlineHtml(Wikilinks.AutoLink(f.Text, names), titles, markdown), f.Verified, f.Evidence, f.Check }),
            });
        }));
        app.MapPatch("/api/concepts/{id}", (string id, ConceptPatch r) => Command(session, c => c.PatchConcept(id, r)));
        app.MapPut("/api/concepts/{id}/facts", (string id, List<FactInput> r) => Command(session, c => c.PutFacts(id, r)));
        app.MapPut("/api/concepts/{id}/teaching", (string id, TeachingRequest r) => Command(session, c => c.PutTeaching(id, r.Summary, r.Explanation)));
        app.MapPost("/api/concepts/{id}/requests", (string id, RequestInput r) => Command(session, c => c.PostRequest(id, r)));
        app.MapPut("/api/concepts/{id}/recall-questions", (string id, List<RecallQuestionInput> r) => Command(session, c => c.PutRecallQuestions(id, r)));
        app.MapPut("/api/concepts/{id}/check", (string id, CheckRequest r) => Command(session, c => c.PutCheck(id, new CheckInput(r.Facts, r.Teaching), r.Session)));

        // ----- conversations -----
        app.MapPost("/api/questions", (QuestionInput r) => Command(session, c => c.PostQuestion(r)));
        app.MapGet("/api/conversations", (string? concept, int? limit) => WithStore(session, s =>
            Results.Json(RenderConversations(s, s.Queries.ListConversations(concept, limit ?? 50), markdown))));
        app.MapGet("/api/conversations/{id}", (string id) => WithStore(session, s =>
            s.Queries.GetConversation(id) is { } conv ? Results.Json(RenderConversations(s, [conv], markdown)[0]) : Results.NotFound()));
        app.MapPost("/api/conversations/{id}/replies", (string id, ReplyRequest r) => Command(session, c => c.PostReply(id, r.Text, r.Session)));
        app.MapGet("/api/pages", async (string url) =>
        {
            try { return Results.Text((await fetcher.FetchAsync(url)).Content, "text/plain; charset=utf-8"); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or ArgumentException or TaskCanceledException)
            {
                return Results.Json(new { accepted = false, violation = "fetch-failed", message = ex.Message }, statusCode: 422);
            }
        });
        app.MapGet("/api/concepts/{id}/neighbors", (string id) => WithStore(session, s => Results.Json(s.Queries.Neighbors(id))));
        app.MapGet("/api/concepts/{id}/graph", (string id, int? hops) => WithStore(session, s =>
            Results.Json(s.Queries.GraphAround(id, Math.Clamp(hops ?? 2, 1, 4)))));

        // ----- edges -----
        app.MapGet("/api/edges/{from}/{to}", (string from, string to) => WithStore(session, s =>
            s.Queries.Relation(from, to) is { } rel ? Results.Json(rel) : Results.NotFound()));
        app.MapPost("/api/edges", (EdgeRequest r) => Command(session, c => c.PostEdge(r.From, r.Type, r.To, r.Evidence ?? [])));
        app.MapDelete("/api/edges/{from}/{to}", (string from, string to) => Command(session, c => c.DeleteEdge(from, to)));
        app.MapPost("/api/edges/{from}/{to}/evidence", (string from, string to, EvidenceIdsRequest r) =>
            Command(session, c => c.PostEdgeEvidence(from, to, r.Evidence ?? [])));

        // ----- evidence -----
        app.MapGet("/api/evidence", () => WithStore(session, s => Results.Json(s.Queries.ListEvidence())));
        app.MapGet("/api/evidence/{id}", (string id) => WithStore(session, s =>
            s.Queries.GetEvidence(id) is { } e ? Results.Json(e) : Results.NotFound()));
        app.MapPost("/api/evidence", async (EvidenceInput r) =>
            session.Commands is { } c ? CommandResultJson(await c.PostEvidenceAsync(r)) : NoStore());
        app.MapDelete("/api/evidence/{id}", (string id) => Command(session, c => c.DeleteEvidence(id)));
        app.MapPost("/api/evidence/{id}/verifications", async (string id) =>
            session.Commands is { } c ? CommandResultJson(await c.PostVerificationAsync(id)) : NoStore());
        app.MapPost("/api/evidence/{id}/confirmations", (string id, ConfirmationRequest r) =>
            Command(session, c => c.PostConfirmation(id, r.Snapshot, r.MediaType ?? "text/plain", r.Session)));
        app.MapGet("/api/snapshots/{hash}", (string hash) => WithStore(session, s =>
            s.Queries.GetSnapshot(hash) is { } snap ? Results.Json(snap) : Results.NotFound()));

        // ----- learner -----
        app.MapGet("/api/learner/visits", (int? limit) => WithStore(session, s => Results.Json(s.Queries.Journey(limit ?? 50))));
        app.MapPost("/api/learner/visits", (VisitRequest r) => Command(session, c => c.PostVisit(r.ConceptId, r.From, r.Direction)));
        app.MapGet("/api/learner/known", () => WithStore(session, s => Results.Json(s.Queries.Known())));
        app.MapPut("/api/learner/known/{id}", (string id) => Command(session, c => c.PutKnown(id)));
        app.MapDelete("/api/learner/known/{id}", (string id) => Command(session, c => c.DeleteKnown(id)));
        app.MapPost("/api/learner/recall-answers", (RecallAnswerRequest r) =>
            Command(session, c => c.PostRecallAnswer(r.ConceptId, r.Answer, r.Feedback, r.QuestionIndex, r.Correct)));

        // ----- work queue: messages and leases -----
        app.MapGet("/api/messages", (string? state) => WithStore(session, s => state switch
        {
            null => Results.Json(s.Queue.List()),
            "dead" => Results.Json(s.Queue.DeadLetters()),
            "ready" or "leased" or "done" => Results.Json(s.Queue.List(includeDone: true).Where(m => m.State == state)),
            _ => Results.Json(new { error = "state must be ready, leased, done or dead." }, statusCode: 400),
        }));
        app.MapPost("/api/queues/{queue}/leases", (string queue, LeaseRequest? r) => WithStore(session, s =>
        {
            if (!Policies.Queues.All.Contains(queue)) return Results.NotFound();
            var m = s.Queue.TryReceive([queue], TimeSpan.FromSeconds(r?.LeaseSeconds ?? 1800), r?.Session ?? "http");
            return m is null ? Results.NoContent() : Results.Json(m);
        }));
        app.MapDelete("/api/leases/{receipt}", (string receipt, string? withoutOutcome) => WithStore(session, s =>
        {
            var done = WorkOutcomes.Complete(s, receipt, withoutOutcome);
            return done.Accepted ? Results.NoContent() : CommandResultJson(done);
        }));
        app.MapPatch("/api/leases/{receipt}", (string receipt, LeasePatch r) => WithStore(session, s =>
            !r.Release ? Results.Json(new { error = "A lease patch can only release: send {\"release\": true}." }, statusCode: 400)
            : s.Queue.Nack(receipt, r.Error) ? Results.NoContent() : LeaseLost()));

        // ----- events and the agent brief -----
        app.MapGet("/api/events", (long? after, int? limit) => WithStore(session, s =>
            Results.Json(s.Events.Read(after ?? 0, null, Math.Clamp(limit ?? 100, 1, 1000)))));
        // ----- agent connection (agents attach themselves; this reports who is connected) -----
        app.MapGet("/api/agents", () => WithStore(session, s => Results.Json(AgentsPayload(s))));

        app.MapGet("/api/brief", () => WithStore(session, store =>
            Results.Text(AgentBrief.Render(store.Queries.GetInfo(), exe), "text/plain; charset=utf-8")));

        // Live updates: the store file is the bus. Poll the event log and forward new events as server-sent events.
        app.MapGet("/api/events/stream", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var store = session.Current;
            var after = store?.Events.LatestSeq() ?? 0;
            string? lastAgents = null;
            var tick = 0;
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
                // Agent presence is not in the event log; send it whenever it changes (checked every second).
                if (++tick % 2 == 0)
                {
                    var agents = JsonSerializer.Serialize(AgentsPayload(store), Json.Options);
                    if (agents != lastAgents)
                    {
                        lastAgents = agents;
                        await ctx.Response.WriteAsync($"event: agents\ndata: {agents}\n\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    }
                }
                var events = store.Events.Read(after, null, 200);
                if (events.Count == 0) continue;
                after = events[^1].Seq;
                var data = JsonSerializer.Serialize(events.Select(e => new { e.Seq, e.Type, e.Stream }), Json.Options);
                await ctx.Response.WriteAsync($"event: changes\ndata: {data}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        });

        app.Lifetime.ApplicationStopping.Register(session.Close);
        var url = $"http://localhost:{options.Port}";
        Console.WriteLine($"b-learn UI at {url}" + (session.Current is null ? " (choose a store)" : $" ({session.Current.Path})"));
        if (options.OpenBrowser) OpenBrowser(url);
        await app.RunAsync();
    }

    /// <summary>Connection state for the UI: which agents are connected and what they are doing.</summary>
    private static object AgentsPayload(IStore store)
    {
        var status = store.Agents.Status();
        return new
        {
            status.Connected,
            agents = status.Agents.Where(a => a.State != AgentStates.Offline).Select(a => new { a.Session, a.State, a.Activity, a.Work }),
        };
    }

    /// <summary>The blearn command agents should run: this executable when it is blearn itself.</summary>
    private static string AgentExe(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;
        var self = Environment.ProcessPath;
        return self is not null && Path.GetFileNameWithoutExtension(self).Equals("blearn", StringComparison.OrdinalIgnoreCase) ? self : configured;
    }

    private static IResult WithStore(StoreSession session, Func<IStore, IResult> act) =>
        session.Current is { } store ? act(store) : NoStore();

    private static IResult NoStore() => Results.Json(new { error = "No store is open." }, statusCode: 409);

    private static IResult LeaseLost() =>
        Results.Json(new { accepted = false, violation = "lease-lost", message = "Unknown receipt, or the lease expired." }, statusCode: 422);

    private static IResult Command(StoreSession session, Func<BLearnCommands, CommandResult> act) =>
        session.Commands is { } c ? CommandResultJson(act(c)) : NoStore();

    private static IResult CommandResultJson(CommandResult r) => Results.Json(r, statusCode: r.Accepted ? 200 : 422);

    private static IResult Guard(Func<IResult> act)
    {
        try { return act(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
    }

    /// <summary>Adds rendered HTML to each message; mentions of any concept in the store become links.</summary>
    private static List<object> RenderConversations(IStore store, IReadOnlyList<ConversationView> conversations, MarkdownPipeline pipeline)
    {
        if (conversations.Count == 0) return [];
        var titles = store.Queries.ListConcepts().ToDictionary(x => x.Id, x => (x.Title, x.Status));
        var names = store.Queries.ConceptNames();
        return conversations.Select(c =>
        {
            var others = names.Where(n => n.Id != c.ConceptId).ToList();
            return (object)new
            {
                c.Id, c.ConceptId, c.ConceptTitle, c.Quote, c.FactIndex, c.FactText, c.StartedAt, c.AwaitingReply,
                messages = c.Messages.Select(m => new
                {
                    m.Ordinal, m.Role, m.Text, m.At,
                    html = m.Role == "agent" ? Render(Wikilinks.AutoLink(m.Text, others), titles, pipeline) : WebUtility.HtmlEncode(m.Text),
                }),
            };
        }).ToList();
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
/// <param name="RootGloss">Optional; a placeholder is used and the first research replaces it.</param>
/// <param name="WebSource">Register the public web as a source of truth (default true).</param>
public sealed record CreateStoreRequest(string Path, string Name, string RootTitle, string? RootGloss, bool Private, bool? WebSource);
public sealed record StorePatch(bool? Private);
public sealed record SourceRequest(string Name, string System, string Scope, string? MinTrust);
public sealed record FolderRequest(string Folder);
public sealed record TeachingRequest(string Summary, string Explanation);
public sealed record EdgeRequest(string From, string Type, string To, IReadOnlyList<string>? Evidence);
public sealed record EvidenceIdsRequest(IReadOnlyList<string>? Evidence);
public sealed record ConfirmationRequest(string Snapshot, string? MediaType, string Session);
public sealed record VisitRequest(string ConceptId, string? From, string? Direction);
public sealed record RecallAnswerRequest(string ConceptId, string Answer, string? Feedback, int? QuestionIndex, bool? Correct);
public sealed record ReplyRequest(string Text, string Session);
public sealed record CheckRequest(IReadOnlyList<FactCheckInput>? Facts, IReadOnlyList<TeachingFlagInput>? Teaching, string Session);
public sealed record LeaseRequest(string? Session, int? LeaseSeconds);
public sealed record LeasePatch(bool Release, string? Error);

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

    public const string DefaultWebScope = "Public web pages: official documentation, standards and encyclopedias";
    public const string PlaceholderGloss = "Not researched yet.";

    public CommandResult Create(string path, string name, string rootTitle, string? rootGloss, bool isPrivate, bool webSource)
    {
        lock (_lock)
        {
            var store = provider.Create(path);
            var commands = new BLearnCommands(store, clock, fetcher);
            var result = commands.PostStore(name, rootTitle, string.IsNullOrWhiteSpace(rootGloss) ? PlaceholderGloss : rootGloss, isPrivate);
            if (result.Accepted && webSource) result = commands.PostSource("web", "web", DefaultWebScope);
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
