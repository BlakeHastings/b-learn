using System.Text.Json;
using System.Text.Json.Nodes;
using BLearn.Application;
using BLearn.Cli;
using BLearn.Domain;
using BLearn.Infrastructure.Fetching;
using BLearn.Infrastructure.Markdown;
using BLearn.Infrastructure.Sqlite;
using BLearn.Web;

return await Cli.RunAsync(args);

namespace BLearn.Cli
{
    public static class ExitCodes
    {
        public const int Ok = 0;
        public const int Rejected = 1;
        public const int Usage = 2;
        public const int Timeout = 3;
        public const int NotFound = 4;
    }

    /// <summary>
    /// The CLI adapter. Commands are <c>blearn &lt;resource&gt; &lt;verb&gt;</c> with the HTTP verbs
    /// get, list, post, put, patch and delete, mirroring the HTTP API.
    /// </summary>
    public static class Cli
    {
        private static readonly JsonSerializerOptions Pretty = new(Json.Options) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        private static readonly IClock Clock = new SystemClock();
        private static readonly IStoreProvider Provider = new SqliteStoreProvider(Clock);

        public static async Task<int> RunAsync(string[] argv)
        {
            try
            {
                argv = HoistGlobalOptions(argv);
                if (argv.Length == 0) return await Serve(new CliArgs([]));
                var (resource, verb) = (argv[0], argv.Length > 1 ? argv[1] : "");
                if (resource is "help" or "--help" or "-h") return argv.Length > 1 && !argv[1].StartsWith("--") ? Help(argv[1]) : Help();
                if (verb is "help" or "--help" or "-h" || argv.Contains("--help")) return Help(resource);
                if (resource == "serve") return await Serve(new CliArgs(argv.Skip(1).ToArray()));
                (resource, verb) = Alias(resource, verb);
                return await Dispatch(resource, verb, new CliArgs(argv.Skip(2).ToArray()));
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}\nRun 'blearn help' for usage.");
                return ExitCodes.Usage;
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException)
            {
                return Emit(new { error = ex.Message }, ExitCodes.NotFound);
            }
        }

        /// <summary>Familiar queue words kept as aliases for the lease resource.</summary>
        private static (string, string) Alias(string resource, string verb) => (resource, verb) switch
        {
            ("queue", "receive") => ("lease", "post"),
            ("queue", "ack") => ("lease", "delete"),
            ("queue", "nack") => ("lease", "release"),
            _ => (resource, verb),
        };

        /// <summary>
        /// Moves global options (--store, --session) that appear before the command to the end, so
        /// "blearn --store F lease post" and "blearn lease post --store F" mean the same thing.
        /// </summary>
        private static string[] HoistGlobalOptions(string[] argv)
        {
            var rest = new List<string>();
            var globals = new List<string>();
            var i = 0;
            while (i < argv.Length && argv[i].StartsWith("--"))
            {
                var name = argv[i];
                var bare = name.Split('=')[0];
                if (bare is not ("--store" or "--session" or "--port" or "--no-open")) break;
                globals.Add(name);
                if (!name.Contains('=') && bare != "--no-open")
                {
                    if (i + 1 >= argv.Length) throw new UsageException($"{bare} needs a value.");
                    globals.Add(argv[++i]);
                }
                i++;
            }
            rest.AddRange(argv.Skip(i));
            if (rest.Count == 0 && globals.Count > 0) rest.Add("serve");
            rest.AddRange(globals);
            return rest.ToArray();
        }

        private static async Task<int> Dispatch(string resource, string verb, CliArgs a)
        {
            // Commands that need no open store.
            switch (resource, verb)
            {
                case ("store", "post"): return StorePost(a);
                case ("store", "list"): a.Flag("recent"); a.EnsureAllUsed(); return Emit(RecentStores.Load());
                case ("page", "get"): return await PageGet(a);
                case ("edge-type", "list"):
                    a.EnsureAllUsed();
                    return Emit(EdgeTypes.List.Select(t => new { t.Name, direction = t.Direction.ToString().ToLowerInvariant(), t.InverseLabel, t.Meaning }));
            }

            using var store = OpenStore(a);
            if ((a.Opt("session") ?? Environment.GetEnvironmentVariable("BLEARN_SESSION")) is { Length: > 0 } alive && !(resource == "lease" && verb == "post"))
                store.Agents.Heartbeat(alive);
            var cmd = new BLearnCommands(store, Clock, new SourceFetcher(SourceFetcher.CreateClient()));
            var q = store.Queries;

            switch (resource, verb)
            {
                // ----- store, sources, import, export -----
                case ("store", "get"): a.EnsureAllUsed(); return Emit(q.GetInfo());
                case ("store", "patch"):
                {
                    var value = a.Opt("private");
                    a.EnsureAllUsed();
                    bool? isPrivate = value is null ? null : bool.TryParse(value, out var b) ? b : throw new UsageException("--private must be true or false.");
                    return Result(cmd.PatchStore(isPrivate));
                }
                case ("source", "post"):
                {
                    var r = cmd.PostSource(a.At(0, "source name"), a.Req("system"), a.Req("scope"), a.Opt("min-trust") ?? "captured");
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("source", "list"): a.EnsureAllUsed(); return Emit(q.GetInfo().Sources);
                case ("import", "post"):
                {
                    var (notes, problems) = LegacyMarkdownReader.ReadFolder(a.At(0, "markdown folder"));
                    a.EnsureAllUsed();
                    if (problems.Count > 0) return Emit(new { accepted = false, violation = "parse-failed", problems }, ExitCodes.Rejected);
                    return Result(cmd.PostImport(notes));
                }
                case ("export", "post"):
                {
                    var folder = a.At(0, "output folder");
                    a.EnsureAllUsed();
                    return Emit(new { exported = MarkdownExporter.Export(q, folder), folder = Path.GetFullPath(folder) });
                }

                // ----- concepts -----
                case ("concept", "get"):
                {
                    var id = a.At(0, "concept id");
                    a.EnsureAllUsed();
                    return q.GetConcept(id) is { } c ? Emit(c) : NotFound($"Concept '{id}' does not exist.");
                }
                case ("concept", "list"):
                {
                    var (status, search, limit) = (a.Opt("status"), a.Opt("search"), a.Int("limit") ?? 20);
                    a.EnsureAllUsed();
                    if (search is null) return Emit(q.ListConcepts(status));
                    var found = q.Search(search, limit);
                    return Emit(status is null ? found : found.Where(c => c.Status == status));
                }
                case ("concept", "post"):
                {
                    var from = a.Opt("from");
                    var link = from is null ? null : new ConceptLink(from, a.Req("type"), a.List("evidence"));
                    var r = cmd.PostConcept(new ConceptInput(a.Opt("id"), a.Req("title"), a.Req("gloss"), a.All("alias"), link));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("concept", "patch"):
                {
                    var r = cmd.PatchConcept(a.At(0, "concept id"), new ConceptPatch(a.Opt("title"), a.Opt("gloss"), a.All("add-alias")));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("facts", "put"):
                {
                    var id = a.At(0, "concept id");
                    var facts = ReadJson<List<FactInput>>(a, "facts") ?? [];
                    a.EnsureAllUsed();
                    return Result(cmd.PutFacts(id, facts));
                }
                case ("teaching", "put"):
                {
                    var id = a.At(0, "concept id");
                    var summary = a.Text("summary") ?? throw new UsageException("Missing --summary or --summary-file.");
                    var explanation = a.Text("explanation") ?? throw new UsageException("Missing --explanation or --explanation-file.");
                    a.EnsureAllUsed();
                    return Result(cmd.PutTeaching(id, summary, explanation));
                }
                case ("request", "post"):
                {
                    var r = cmd.PostRequest(a.At(0, "concept id"), new RequestInput(a.Req("kind"), a.Int("fact"), a.Opt("text")));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("recall-question", "put"):
                {
                    var id = a.At(0, "concept id");
                    var questions = ReadJson<List<RecallQuestionInput>>(a, "recall questions") ?? [];
                    a.EnsureAllUsed();
                    return Result(cmd.PutRecallQuestions(id, questions));
                }
                case ("check", "put"):
                {
                    var id = a.At(0, "concept id");
                    var input = ReadJson<CheckInput>(a, "check") ?? new CheckInput([], []);
                    var session = Session(a);
                    a.EnsureAllUsed();
                    return Result(cmd.PutCheck(id, input, session));
                }

                // ----- conversations -----
                case ("question", "post"):
                {
                    var r = cmd.PostQuestion(new QuestionInput(a.At(0, "concept id"), a.Text("text") ?? throw new UsageException("Missing --text or --text-file."),
                        a.Opt("quote"), a.Int("fact"), a.Opt("conversation")));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("reply", "post"):
                {
                    var id = a.At(0, "conversation id");
                    var text = a.Text("text") ?? throw new UsageException("Missing --text or --text-file.");
                    var r = cmd.PostReply(id, text, Session(a));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("conversation", "get"):
                {
                    var id = a.At(0, "conversation id");
                    a.EnsureAllUsed();
                    return q.GetConversation(id) is { } conv ? Emit(conv) : NotFound($"Conversation '{id}' does not exist.");
                }
                case ("conversation", "list"):
                {
                    var (concept, waiting, limit) = (a.Opt("concept"), a.Flag("waiting"), a.Int("limit") ?? 50);
                    a.EnsureAllUsed();
                    var list = q.ListConversations(concept, limit);
                    return Emit(waiting ? list.Where(c => c.AwaitingReply) : list);
                }

                case ("neighbor", "list"): { var id = a.At(0, "concept id"); a.EnsureAllUsed(); return Emit(q.Neighbors(id)); }
                case ("graph", "get"):
                {
                    var id = a.At(0, "concept id");
                    var hops = a.Int("hops") ?? 2;
                    a.EnsureAllUsed();
                    return Emit(q.GraphAround(id, hops));
                }

                // ----- edges -----
                case ("edge", "get"):
                {
                    var (from, to) = (a.At(0, "source id"), a.At(1, "target id"));
                    a.EnsureAllUsed();
                    return q.Relation(from, to) is { } rel ? Emit(rel) : NotFound($"No edge between '{from}' and '{to}'.");
                }
                case ("edge", "post"):
                {
                    var r = cmd.PostEdge(a.At(0, "source id"), a.At(1, "edge type"), a.At(2, "target id"), a.List("evidence"));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("edge", "delete"): { var r = cmd.DeleteEdge(a.At(0, "source id"), a.At(1, "target id")); a.EnsureAllUsed(); return Result(r); }
                case ("edge-evidence", "post"):
                {
                    var r = cmd.PostEdgeEvidence(a.At(0, "source id"), a.At(1, "target id"), a.List("evidence"));
                    a.EnsureAllUsed();
                    return Result(r);
                }

                // ----- evidence -----
                case ("evidence", "get"):
                {
                    var id = a.At(0, "evidence id");
                    a.EnsureAllUsed();
                    return q.GetEvidence(id) is { } e ? Emit(e) : NotFound($"Evidence '{id}' does not exist.");
                }
                case ("evidence", "list"): a.EnsureAllUsed(); return Emit(q.ListEvidence());
                case ("evidence", "post"):
                {
                    var id = a.At(0, "evidence id");
                    var snapshotFile = a.Opt("snapshot-file");
                    var input = new EvidenceInput(id, a.Req("source"), a.Req("quote"), Session(a),
                        Locator: a.Opt("locator"),
                        SnapshotText: snapshotFile is null ? null : snapshotFile == "-" ? Console.In.ReadToEnd() : File.ReadAllText(snapshotFile),
                        MediaType: a.Opt("media-type"),
                        Url: a.Opt("url"));
                    a.EnsureAllUsed();
                    return Result(await cmd.PostEvidenceAsync(input));
                }
                case ("evidence", "delete"): { var r = cmd.DeleteEvidence(a.At(0, "evidence id")); a.EnsureAllUsed(); return Result(r); }
                case ("snapshot", "get"):
                {
                    var hash = a.At(0, "snapshot hash");
                    var textOnly = a.Flag("text");
                    a.EnsureAllUsed();
                    if (q.GetSnapshot(hash) is not { } s) return NotFound($"No snapshot '{hash}'.");
                    if (!textOnly) return Emit(s);
                    Console.WriteLine(s.Content);
                    return ExitCodes.Ok;
                }
                case ("verification", "post"):
                {
                    var all = a.Flag("all");
                    var id = a.AtOrNull(0);
                    a.EnsureAllUsed();
                    if (!all && id is null) throw new UsageException("Give an evidence id or --all.");
                    var ids = all ? q.ListEvidence().Select(e => e.Id).ToList() : [id!];
                    var results = new List<object>();
                    var worst = ExitCodes.Ok;
                    foreach (var e in ids)
                    {
                        var r = await cmd.PostVerificationAsync(e);
                        if (!r.Accepted) worst = ExitCodes.Rejected;
                        results.Add(new { evidence = e, result = Shape(r) });
                    }
                    return Emit(results, worst);
                }
                case ("confirmation", "post"):
                {
                    var id = a.At(0, "evidence id");
                    var snapshot = File.ReadAllText(a.Req("snapshot-file"));
                    var r = cmd.PostConfirmation(id, snapshot, a.Opt("media-type") ?? "text/plain", Session(a));
                    a.EnsureAllUsed();
                    return Result(r);
                }

                // ----- leases (the work queue) and messages -----
                case ("lease", "post"): return await LeasePost(store, a);
                case ("lease", "delete"):
                {
                    var receipt = a.At(0, "receipt");
                    var without = a.Opt("without-outcome");
                    a.EnsureAllUsed();
                    var done = WorkOutcomes.Complete(store, receipt, without);
                    return done.Accepted ? Emit(new { done = receipt }) : Result(done);
                }
                case ("lease", "patch"):
                case ("lease", "release"):
                {
                    var receipt = a.At(0, "receipt");
                    if (verb == "patch" && !a.Flag("release")) throw new UsageException("lease patch changes one thing: pass --release to hand the message back.");
                    var error = a.Opt("error");
                    a.EnsureAllUsed();
                    return store.Queue.Nack(receipt, error)
                        ? Emit(new { released = receipt })
                        : Emit(new { accepted = false, violation = "lease-lost", message = "Unknown receipt, or the lease already expired." }, ExitCodes.Rejected);
                }
                case ("message", "list"):
                {
                    var state = a.Opt("state");
                    a.EnsureAllUsed();
                    var messages = state switch
                    {
                        null => store.Queue.List(),
                        "dead" => store.Queue.DeadLetters(),
                        "ready" or "leased" or "done" => store.Queue.List(includeDone: true).Where(m => m.State == state).ToList(),
                        _ => throw new UsageException("--state must be ready, leased, done or dead."),
                    };
                    return Emit(messages.Select(MessageView));
                }

                // ----- events -----
                case ("event", "list"): return await EventList(store, a);

                // ----- learner -----
                case ("visit", "post"):
                {
                    var r = cmd.PostVisit(a.At(0, "concept id"), a.Opt("from"), a.Opt("direction"));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("visit", "list"): { var l = a.Int("limit") ?? 100; a.EnsureAllUsed(); return Emit(q.Journey(l)); }
                case ("known", "put"): { var r = cmd.PutKnown(a.At(0, "concept id")); a.EnsureAllUsed(); return Result(r); }
                case ("known", "delete"): { var r = cmd.DeleteKnown(a.At(0, "concept id")); a.EnsureAllUsed(); return Result(r); }
                case ("known", "list"): a.EnsureAllUsed(); return Emit(q.Known());
                case ("recall-answer", "post"):
                {
                    var correct = a.Opt("correct") is { } ok ? bool.TryParse(ok, out var b2) ? b2 : throw new UsageException("--correct must be true or false.") : (bool?)null;
                    var r = cmd.PostRecallAnswer(a.At(0, "concept id"), a.Req("answer"), a.Opt("feedback"), a.Int("question"), correct);
                    a.EnsureAllUsed();
                    return Result(r);
                }

                // ----- agent brief -----
                case ("brief", "get"):
                    a.EnsureAllUsed();
                    Console.WriteLine(AgentBrief.Render(q.GetInfo(), ExeName()));
                    return ExitCodes.Ok;

                default:
                    throw new UsageException($"Unknown command '{resource} {verb}'.");
            }
        }

        // ---------- store ----------

        private static int StorePost(CliArgs a)
        {
            var path = a.At(0, "store file path");
            var name = a.Req("name");
            var rootTitle = a.Req("root-title");
            var rootGloss = a.Req("root-gloss");
            var rootId = a.Opt("root-id");
            var isPrivate = a.Flag("private");
            a.EnsureAllUsed();
            if (!path.EndsWith(".blearn", StringComparison.OrdinalIgnoreCase)) path += ".blearn";
            using var store = Provider.Create(path);
            var r = new BLearnCommands(store, Clock, new SourceFetcher(SourceFetcher.CreateClient()))
                .PostStore(name, rootTitle, rootGloss, isPrivate, rootId);
            if (!r.Accepted)
            {
                store.Dispose();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                File.Delete(store.Path);
                return Result(r);
            }
            RecentStores.Touch(store.Path, name);
            return Emit(new { accepted = true, store = store.Path, events = r.Events.Select(EventView) });
        }

/// <summary>Prints the text blearn would store as a snapshot for a URL or file, so quotes can be copied from it exactly.</summary>
        private static async Task<int> PageGet(CliArgs a)
        {
            var url = a.Opt("url") ?? a.AtOrNull(0) ?? throw new UsageException("Missing --url.");
            a.EnsureAllUsed();
            try
            {
                var page = await new SourceFetcher(SourceFetcher.CreateClient()).FetchAsync(url);
                Console.WriteLine(page.Content);
                return ExitCodes.Ok;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or ArgumentException or TaskCanceledException)
            {
                return Emit(new { accepted = false, violation = "fetch-failed", message = $"Could not fetch '{url}': {ex.Message}" }, ExitCodes.Rejected);
            }
        }

        /// <summary>Reads a JSON body from --file (or "-" for stdin) or --json.</summary>
        private static T? ReadJson<T>(CliArgs a, string what)
        {
            var json = a.Opt("json") ?? (a.Opt("file") is { } f ? (f == "-" ? Console.In.ReadToEnd() : File.ReadAllText(f)) : null)
                ?? throw new UsageException($"Provide the {what} with --file <path> or --json '<json>'.");
            try { return JsonSerializer.Deserialize<T>(json, Json.Options); }
            catch (JsonException ex) { throw new UsageException($"The {what} JSON is invalid: {ex.Message}"); }
        }

        /// <summary>The store from --store, BLEARN_STORE, or else the store open in the running b-learn UI.</summary>
        private static IStore OpenStore(CliArgs a)
        {
            var path = a.Opt("store") ?? Environment.GetEnvironmentVariable("BLEARN_STORE") ?? StoreOpenInUi(a.Int("port") ?? 5170)
                ?? throw new UsageException("No store given. Open one in the b-learn UI, or pass --store <file.blearn>, or set BLEARN_STORE.");
            return Provider.Open(path);
        }

        private static string? StoreOpenInUi(int port)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var doc = JsonDocument.Parse(http.GetStringAsync($"http://localhost:{port}/api/session").GetAwaiter().GetResult());
                return doc.RootElement.TryGetProperty("store", out var s) && s.ValueKind == JsonValueKind.Object ? s.GetProperty("path").GetString() : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
        }

        private static string Session(CliArgs a) =>
            a.Opt("session") ?? Environment.GetEnvironmentVariable("BLEARN_SESSION")
            ?? throw new UsageException("Pass --session <id> or set BLEARN_SESSION, so evidence records who captured it.");

        // ---------- waiting ----------

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>Claims one message: <c>lease post [queue...] [--wait] [--timeout SEC]</c>.</summary>
        private static async Task<int> LeasePost(IStore store, CliArgs a)
        {
            var queues = a.Positional.Concat(a.List("queue")).ToList();
            var wait = a.Flag("wait");
            var timeout = a.Int("timeout");
            var lease = TimeSpan.FromSeconds(a.Int("lease") ?? 1800);
            var receiver = a.Opt("session") ?? Environment.GetEnvironmentVariable("BLEARN_SESSION") ?? $"agent-{Environment.ProcessId}";
            a.EnsureAllUsed();
            foreach (var queue in queues)
                if (!Policies.Queues.All.Contains(queue))
                    throw new UsageException($"Unknown queue '{queue}'. Queues: {string.Join(", ", Policies.Queues.All)}.");

            var deadline = timeout is { } t ? DateTimeOffset.Now.AddSeconds(t) : (DateTimeOffset?)null;
            while (true)
            {
                if (store.Queue.TryReceive(queues, lease, receiver) is { } m) return Emit(MessageView(m));
                if (!wait) return Emit(new { message = (object?)null, note = "Queue is empty." });
                if (deadline is { } d && DateTimeOffset.Now >= d) return Emit(new { timeout = true }, ExitCodes.Timeout);
                await Task.Delay(PollInterval);
            }
        }

        /// <summary><c>event list [--after N] [--type T,...] [--limit N]</c>; with <c>--wait</c>, blocks until there is at least one.</summary>
        private static async Task<int> EventList(IStore store, CliArgs a)
        {
            var types = a.List("type");
            var wait = a.Flag("wait");
            var afterOpt = (long?)a.Int("after");
            var limit = a.Int("limit") ?? 100;
            var timeout = a.Int("timeout");
            a.EnsureAllUsed();
            if (!wait)
            {
                if (timeout is not null) throw new UsageException("--timeout only applies with --wait.");
                return Emit(store.Events.Read(afterOpt ?? 0, types, limit).Select(EventView));
            }

            var after = afterOpt ?? store.Events.LatestSeq();
            var deadline = timeout is { } t ? DateTimeOffset.Now.AddSeconds(t) : (DateTimeOffset?)null;
            while (true)
            {
                var events = store.Events.Read(after, types, limit);
                if (events.Count > 0) return Emit(new { events = events.Select(EventView), next = events[^1].Seq });
                if (deadline is { } d && DateTimeOffset.Now >= d) return Emit(new { timeout = true, next = after }, ExitCodes.Timeout);
                await Task.Delay(PollInterval);
            }
        }

        // ---------- serve ----------

        private static async Task<int> Serve(CliArgs a)
        {
            var store = a.Opt("store") ?? Environment.GetEnvironmentVariable("BLEARN_STORE");
            var port = a.Int("port") ?? 5170;
            var open = !a.Flag("no-open");
            a.EnsureAllUsed();
            await WebApp.RunAsync(new WebAppOptions(store, port, open, ExeName()));
            return ExitCodes.Ok;
        }

        // ---------- output ----------

        private static string ExeName() => Environment.GetEnvironmentVariable("BLEARN_EXE")
            ?? (Environment.ProcessPath is { } p && Path.GetFileNameWithoutExtension(p).Equals("blearn", StringComparison.OrdinalIgnoreCase) ? p.Replace('\\', '/') : "blearn");

        private static object EventView(StoredEvent e) =>
            new { e.Seq, e.Type, e.Stream, e.At, payload = JsonNode.Parse(e.PayloadJson) };

        private static object MessageView(QueueMessage m) =>
            new { m.Id, m.Queue, m.DedupKey, payload = JsonNode.Parse(m.PayloadJson), m.State, m.Attempts, m.CreatedAt, m.LeaseUntil, m.Receipt, m.ReceivedBy, m.LastError };

        private static object Shape(CommandResult r) => r.Accepted
            ? new { accepted = true, events = r.Events.Select(EventView) }
            : new { accepted = false, violation = r.Violation, message = r.Message };

        private static int Result(CommandResult r) => Emit(Shape(r), r.Accepted ? ExitCodes.Ok : ExitCodes.Rejected);

        private static int NotFound(string message) => Emit(new { error = message }, ExitCodes.NotFound);

        private static int Emit(object? value, int code = ExitCodes.Ok)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, Pretty));
            return code;
        }

        /// <summary>Prints the lines for one resource, or everything when it is unknown.</summary>
        private static int Help(string resource)
        {
            var lines = HelpText.Split('\n');
            var start = Array.FindIndex(lines, l => l.TrimEnd().StartsWith("  " + resource + " ") || l.Trim() == resource);
            if (start < 0) { Console.Error.WriteLine($"No help for '{resource}'."); return Help(); }
            Console.WriteLine(lines[start].TrimEnd());
            for (var i = start + 1; i < lines.Length && lines[i].StartsWith("                 "); i++) Console.WriteLine(lines[i].TrimEnd());
            Console.WriteLine("\nOutput is JSON. Exit codes: 0 ok, 1 rejected by a rule, 2 usage, 3 wait timed out, 4 not found. Global: --store <file.blearn> (or BLEARN_STORE).");
            return ExitCodes.Ok;
        }

        private static int Help()
        {
            Console.WriteLine(HelpText);
            return ExitCodes.Ok;
        }

        private const string HelpText = """
                blearn: build and explore evidence-backed concept graphs.

                  blearn                                  open the web UI (store picker)
                  blearn serve [--store F] [--port N] [--no-open]

                Commands are 'blearn <resource> <verb>' with the verbs get, list, post, put, patch, delete.
                Every command except 'store post' and 'store list' works on --store <file.blearn>, BLEARN_STORE, or the store open in the b-learn UI.
                Output is JSON. Exit codes: 0 ok, 1 rejected by a rule, 2 usage, 3 wait timed out, 4 not found.

                Store
                  store          post <file> --name N --root-title T --root-gloss G [--root-id I] [--private]
                                 get | list (recent stores) | patch --private true|false
                  source         post <name> --system S --scope "..." [--min-trust captured|reconfirmed|fetched] | list
                  import         post <markdown-folder>
                  export         post <folder>

                Concepts and edges
                  concept        get <id> | list [--status S] [--search TEXT] [--limit N]
                                 post --title T --gloss G [--id I] [--alias A]... [--from P --type E --evidence ev,...]
                                 patch <id> [--title T] [--gloss G] [--add-alias A]...
                  facts          put <concept> --file F | --json '[{"text": "...", "evidence": ["ev"]}]'   (replaces all facts)
                  teaching       put <concept> --summary[-file] ... --explanation[-file] ...
                  request        post <concept> --kind expand|teach|recall
                  recall-question put <concept> --file F   ([{"prompt": "...", "answer": "...", "facts": [0, 2]}]; replaces the quiz)
                  check          put <concept> --file F --session X
                                 ({"facts": [{"index": 0, "verdict": "supports|partial|unsupported", "note": "..."}],
                                  "teaching": [{"excerpt": "verbatim text from gloss or teaching", "note": "..."}]})
                  neighbor       list <concept>
                  graph          get <concept> [--hops N]
                  edge           get <from> <to> | post <from> <type> <to> --evidence ev,... | delete <from> <to>
                  edge-evidence  post <from> <to> --evidence ev,...
                  edge-type      list

                Evidence
                  evidence       get <id> | list | delete <id> (only when nothing cites it)
                                 post <id> --source S --quote Q --session X (--url U | --snapshot-file F --locator L [--media-type M])
                  snapshot       get <hash> [--text]   (--text prints only the stored page text)
                  page           get --url U   (prints the text blearn would store for U; nothing is saved)
                  verification   post <evidence-id> | --all
                  confirmation   post <evidence-id> --snapshot-file F --session X [--media-type M]

                Conversations
                  question       post <concept> --text Q [--quote "highlighted text"] [--fact N] [--conversation C]
                  reply          post <conversation> --text[-file] ... --session X   (answers the latest question)
                  conversation   get <id> | list [--concept C] [--waiting] [--limit N]

                Work and events
                  lease          post [queue...] [--wait] [--timeout SEC] [--lease SEC] [--session X]   (alias: queue receive)
                                 delete <receipt> [--without-outcome "why"] (done; alias: queue ack) | patch <receipt> --release [--error E] (alias: queue nack)
                  message        list [--state ready|leased|done|dead]
                  event          list [--after N] [--type T,...] [--limit N] [--wait [--timeout SEC]]

                Learner
                  visit          post <concept> [--from P] [--direction down|up|lateral] | list [--limit N]
                  known          put <concept> | delete <concept> | list
                  recall-answer  post <concept> --answer A [--feedback F] [--question N] [--correct true|false]

                  brief          get    (the agent brief)
                """;
    }
}
