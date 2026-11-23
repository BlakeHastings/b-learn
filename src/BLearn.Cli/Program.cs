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
                var a = new CliArgs(argv.Skip(2).ToArray());
                var (resource, action) = (argv[0], argv.Length > 1 ? argv[1] : "");
                if (resource is "help" or "--help" or "-h") return Help();
                if (resource == "serve") return await Serve(new CliArgs(argv.Skip(1).ToArray()));
                var code = await Dispatch(resource, action, a);
                return code;
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

        /// <summary>
        /// Moves global options (--store, --session) that appear before the command to the end, so
        /// "blearn --store F queue receive" and "blearn queue receive --store F" mean the same thing.
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

        private static async Task<int> Dispatch(string resource, string action, CliArgs a)
        {
            switch (resource, action)
            {
                case ("store", "create"): return StoreCreate(a);
                case ("store", "recent"): a.EnsureAllUsed(); return Emit(RecentStores.Load());
                case ("edge", "types"):
                    a.EnsureAllUsed();
                    return Emit(EdgeTypes.List.Select(t => new { t.Name, direction = t.Direction.ToString().ToLowerInvariant(), t.InverseLabel, t.Meaning }));
            }

            using var store = OpenStore(a);
            var cmd = new BLearnCommands(store, Clock, new SourceFetcher(SourceFetcher.CreateClient()));
            var q = store.Queries;

            switch (resource, action)
            {
                // ----- store and sources -----
                case ("store", "info"): a.EnsureAllUsed(); return Emit(q.GetInfo());
                case ("store", "export"):
                {
                    var folder = a.At(0, "output folder");
                    a.EnsureAllUsed();
                    return Emit(new { exported = MarkdownExporter.Export(q, folder), folder = Path.GetFullPath(folder) });
                }
                case ("store", "import-markdown"):
                {
                    var (notes, problems) = LegacyMarkdownReader.ReadFolder(a.At(0, "markdown folder"));
                    a.EnsureAllUsed();
                    if (problems.Count > 0) return Emit(new { accepted = false, violation = "parse-failed", problems }, ExitCodes.Rejected);
                    return Result(cmd.ImportLegacy(notes));
                }
                case ("store", "set-private"):
                {
                    var value = a.At(0, "true or false");
                    a.EnsureAllUsed();
                    return Result(cmd.SetPrivate(bool.TryParse(value, out var b) ? b : throw new UsageException("Expected true or false.")));
                }
                case ("source", "register"):
                {
                    var r = cmd.RegisterSource(a.At(0, "source name"), a.Req("system"), a.Req("scope"), a.Opt("min-trust") ?? "captured");
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("source", "list"): a.EnsureAllUsed(); return Emit(q.GetInfo().Sources);

                // ----- concepts -----
                case ("concept", "get"):
                {
                    var id = a.At(0, "concept id");
                    a.EnsureAllUsed();
                    return q.GetConcept(id) is { } c ? Emit(c) : NotFound($"Concept '{id}' does not exist.");
                }
                case ("concept", "list"): { var s = a.Opt("status"); a.EnsureAllUsed(); return Emit(q.ListConcepts(s)); }
                case ("concept", "search"):
                {
                    var text = string.Join(' ', a.Positional);
                    if (text.Length == 0) throw new UsageException("Missing search text.");
                    var limit = a.Int("limit") ?? 20;
                    a.EnsureAllUsed();
                    return Emit(q.Search(text, limit));
                }
                case ("concept", "neighbors"): { var id = a.At(0, "concept id"); a.EnsureAllUsed(); return Emit(q.Neighbors(id)); }
                case ("concept", "graph"):
                {
                    var id = a.At(0, "concept id");
                    var hops = a.Int("hops") ?? 2;
                    a.EnsureAllUsed();
                    return Emit(q.GraphAround(id, hops));
                }
                case ("concept", "stub"):
                {
                    var from = a.Opt("from");
                    var link = from is null ? null : new StubLink(from, a.Req("type"), a.List("evidence"));
                    var r = cmd.CreateStub(a.Opt("id"), a.Req("title"), a.Req("gloss"), a.All("alias"), link);
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("concept", "rename"): { var r = cmd.Rename(a.At(0, "concept id"), a.Req("title")); a.EnsureAllUsed(); return Result(r); }
                case ("concept", "alias"): { var r = cmd.AddAlias(a.At(0, "concept id"), a.At(1, "alias")); a.EnsureAllUsed(); return Result(r); }
                case ("concept", "gloss"): { var r = cmd.ReviseGloss(a.At(0, "concept id"), a.Req("gloss")); a.EnsureAllUsed(); return Result(r); }
                case ("concept", "facts"):
                {
                    var id = a.At(0, "concept id");
                    var json = a.Opt("json") ?? (a.Opt("file") is { } f ? (f == "-" ? Console.In.ReadToEnd() : File.ReadAllText(f)) : null)
                        ?? throw new UsageException("Provide facts with --file <path> or --json '<array>'.");
                    a.EnsureAllUsed();
                    List<FactInput> facts;
                    try { facts = JsonSerializer.Deserialize<List<FactInput>>(json, Json.Options) ?? []; }
                    catch (JsonException ex) { throw new UsageException($"Facts JSON is invalid: {ex.Message}"); }
                    return Result(cmd.SubmitFacts(id, facts.Select(x => x with { Evidence = x.Evidence ?? [] }).ToList()));
                }
                case ("concept", "teaching"):
                {
                    var id = a.At(0, "concept id");
                    var summary = a.Text("summary") ?? throw new UsageException("Missing --summary or --summary-file.");
                    var explanation = a.Text("explanation") ?? throw new UsageException("Missing --explanation or --explanation-file.");
                    a.EnsureAllUsed();
                    return Result(cmd.SubmitTeaching(id, summary, explanation));
                }
                case ("concept", "request"):
                {
                    var id = a.At(0, "concept id");
                    var kind = a.Req("kind");
                    var r = kind switch
                    {
                        "expand" => cmd.RequestExpansion(id),
                        "teach" => cmd.RequestTeaching(id, a.Int("fact")),
                        "ask" => cmd.AskQuestion(id, a.Req("text")),
                        "recall" => cmd.RequestRecall(id),
                        _ => throw new UsageException("--kind must be expand, teach, ask or recall."),
                    };
                    a.EnsureAllUsed();
                    return Result(r);
                }

                // ----- edges -----
                case ("edge", "add"):
                {
                    var r = cmd.AddEdge(a.At(0, "source id"), a.At(1, "edge type"), a.At(2, "target id"), a.List("evidence"));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("edge", "remove"): { var r = cmd.RemoveEdge(a.At(0, "source id"), a.At(1, "target id")); a.EnsureAllUsed(); return Result(r); }
                case ("edge", "cite"):
                {
                    var r = cmd.AttachEdgeEvidence(a.At(0, "source id"), a.At(1, "target id"), a.List("evidence"));
                    a.EnsureAllUsed();
                    return Result(r);
                }

                // ----- evidence -----
                case ("evidence", "capture"):
                {
                    var id = a.At(0, "evidence id");
                    var snapshotFile = a.Req("snapshot-file");
                    var snapshot = snapshotFile == "-" ? Console.In.ReadToEnd() : File.ReadAllText(snapshotFile);
                    var r = cmd.CaptureEvidence(id, a.Req("source"), a.Req("locator"), a.Req("quote"), snapshot,
                        a.Opt("media-type") ?? "text/plain", Session(a));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("evidence", "fetch"):
                {
                    var id = a.At(0, "evidence id");
                    var (source, url, quote, session) = (a.Req("source"), a.Req("url"), a.Req("quote"), Session(a));
                    a.EnsureAllUsed();
                    return Result(await cmd.FetchEvidenceAsync(id, source, url, quote, session));
                }
                case ("evidence", "reconfirm"):
                {
                    var id = a.At(0, "evidence id");
                    var snapshot = File.ReadAllText(a.Req("snapshot-file"));
                    var r = cmd.ReconfirmEvidence(id, snapshot, a.Opt("media-type") ?? "text/plain", Session(a));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("evidence", "verify"):
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
                        var r = await cmd.VerifyEvidenceAsync(e);
                        if (!r.Accepted) worst = ExitCodes.Rejected;
                        results.Add(new { evidence = e, result = Shape(r) });
                    }
                    return Emit(results, worst);
                }
                case ("evidence", "get"):
                {
                    var id = a.At(0, "evidence id");
                    var withSnapshot = a.Flag("snapshot");
                    a.EnsureAllUsed();
                    if (q.GetEvidence(id) is not { } e) return NotFound($"Evidence '{id}' does not exist.");
                    return Emit(withSnapshot ? new { evidence = e, snapshot = q.GetSnapshot(e.SnapshotHash) } : e);
                }
                case ("evidence", "list"): a.EnsureAllUsed(); return Emit(q.ListEvidence());

                // ----- queue -----
                case ("queue", "receive"): return await QueueReceive(store, a);
                case ("queue", "ack"):
                {
                    var receipt = a.At(0, "receipt");
                    a.EnsureAllUsed();
                    return store.Queue.Ack(receipt)
                        ? Emit(new { acked = receipt })
                        : Emit(new { accepted = false, violation = "lease-lost", message = "Unknown receipt, or the lease expired and the message was redelivered." }, ExitCodes.Rejected);
                }
                case ("queue", "nack"):
                {
                    var receipt = a.At(0, "receipt");
                    var error = a.Opt("error");
                    a.EnsureAllUsed();
                    return store.Queue.Nack(receipt, error)
                        ? Emit(new { nacked = receipt })
                        : Emit(new { accepted = false, violation = "lease-lost", message = "Unknown receipt, or the lease already expired." }, ExitCodes.Rejected);
                }
                case ("queue", "list"): { var done = a.Flag("include-done"); a.EnsureAllUsed(); return Emit(store.Queue.List(done).Select(MessageView)); }
                case ("queue", "dead-letters"): a.EnsureAllUsed(); return Emit(store.Queue.DeadLetters().Select(MessageView));

                // ----- events -----
                case ("events", "list"):
                {
                    var after = (long)(a.Int("after") ?? 0);
                    var types = a.List("type");
                    var limit = a.Int("limit") ?? 100;
                    a.EnsureAllUsed();
                    return Emit(store.Events.Read(after, types, limit).Select(EventView));
                }
                case ("events", "wait"): return await EventsWait(store, a);

                // ----- learner -----
                case ("learner", "visit"):
                {
                    var r = cmd.RecordVisit(a.At(0, "concept id"), a.Opt("from"), a.Opt("direction"));
                    a.EnsureAllUsed();
                    return Result(r);
                }
                case ("learner", "known"): { var r = cmd.MarkKnown(a.At(0, "concept id")); a.EnsureAllUsed(); return Result(r); }
                case ("learner", "unknown"): { var r = cmd.UnmarkKnown(a.At(0, "concept id")); a.EnsureAllUsed(); return Result(r); }
                case ("learner", "journey"): { var l = a.Int("limit") ?? 100; a.EnsureAllUsed(); return Emit(new { visits = q.Journey(l), known = q.Known() }); }
                case ("learner", "recall-answer"):
                {
                    var r = cmd.AnswerRecall(a.At(0, "concept id"), a.Req("answer"), a.Opt("feedback"));
                    a.EnsureAllUsed();
                    return Result(r);
                }

                // ----- agent -----
                case ("agent", "brief"):
                    a.EnsureAllUsed();
                    Console.WriteLine(AgentBrief.Render(q.GetInfo(), ExeName()));
                    return ExitCodes.Ok;

                default:
                    throw new UsageException($"Unknown command '{resource} {action}'.");
            }
        }

        // ---------- store ----------

        private static int StoreCreate(CliArgs a)
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
                .InitializeStore(name, rootTitle, rootGloss, isPrivate, rootId);
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

        private static IStore OpenStore(CliArgs a)
        {
            var path = a.Opt("store") ?? Environment.GetEnvironmentVariable("BLEARN_STORE")
                ?? throw new UsageException("No store given. Pass --store <file.blearn> or set BLEARN_STORE.");
            return Provider.Open(path);
        }

        private static string Session(CliArgs a) =>
            a.Opt("session") ?? Environment.GetEnvironmentVariable("BLEARN_SESSION")
            ?? throw new UsageException("Pass --session <id> or set BLEARN_SESSION, so evidence records who captured it.");

        // ---------- waiting ----------

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        private static async Task<int> QueueReceive(IStore store, CliArgs a)
        {
            var queues = a.List("queue");
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

        private static async Task<int> EventsWait(IStore store, CliArgs a)
        {
            var types = a.List("type");
            var after = (long?)a.Int("after") ?? store.Events.LatestSeq();
            var timeout = a.Int("timeout");
            a.EnsureAllUsed();
            var deadline = timeout is { } t ? DateTimeOffset.Now.AddSeconds(t) : (DateTimeOffset?)null;
            while (true)
            {
                var events = store.Events.Read(after, types);
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

        private static string ExeName() => Environment.GetEnvironmentVariable("BLEARN_EXE") ?? "blearn";

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

        private static int Help()
        {
            Console.WriteLine("""
                blearn: build and explore evidence-backed concept graphs.

                  blearn                                  open the web UI (store picker)
                  blearn serve [--store F] [--port N] [--no-open]

                Every other command takes --store <file.blearn> (or BLEARN_STORE) and prints JSON.
                Exit codes: 0 ok, 1 rejected by a rule, 2 usage, 3 wait timed out, 4 not found.

                  store    create <file> --name N --root-title T --root-gloss G [--root-id I] [--private]
                           info | recent | export <folder> | import-markdown <folder> | set-private true|false
                  source   register <name> --system S --scope "..." [--min-trust captured|reconfirmed|fetched] | list
                  concept  get <id> | list [--status S] | search <text> | neighbors <id> | graph <id> [--hops N]
                           stub --title T --gloss G [--id I] [--alias A]... [--from P --type E --evidence ev,...]
                           rename <id> --title T | alias <id> <alias> | gloss <id> --gloss G
                           facts <id> --file F | --json '[{"text": "...", "evidence": ["ev"]}]'
                           teaching <id> --summary[-file] ... --explanation[-file] ...
                           request <id> --kind expand|teach|ask|recall [--fact N] [--text Q]
                  edge     add <from> <type> <to> --evidence ev,... | remove <from> <to> | cite <from> <to> --evidence ev | types
                  evidence capture <id> --source S --locator L --quote Q --snapshot-file F [--media-type M] --session X
                           fetch <id> --source S --url U --quote Q --session X
                           reconfirm <id> --snapshot-file F --session X | verify <id>|--all | get <id> [--snapshot] | list
                  queue    receive [--queue expand,teach,ask,recall] [--wait] [--timeout SEC] [--lease SEC] [--session X]
                           ack <receipt> | nack <receipt> [--error E] | list [--include-done] | dead-letters
                  events   list [--after N] [--type T,...] [--limit N] | wait [--after N] [--type T,...] [--timeout SEC]
                  learner  visit <id> [--from P] [--direction down|up|lateral] | known <id> | unknown <id> | journey
                           recall-answer <id> --answer A [--feedback F]
                  agent    brief
                """);
            return ExitCodes.Ok;
        }
    }
}
