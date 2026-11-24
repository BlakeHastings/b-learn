# Architecture

Status: implemented, except where noted under "Not built yet" at the end.

## Principles

1. **Model output is a lead, never proof.** Facts and relationships need cited evidence from a source of truth.
2. **Deterministic first.** Every rule that code can check is an invariant in the domain, backed by a database constraint where possible. Agent instructions handle judgment calls only, and stay short.
3. **Layers are separate.** The domain knows nothing about storage, the CLI or the UI.
4. **Bring your own harness.** Agents use the CLI from any harness with a shell. Nothing depends on one vendor's protocol.

## Layers

```
   Adapters         BLearn.Cli (agents, scripts)        BLearn.Web (local UI)
                              \                          /
                               v      commands          v
   Application      Command handlers, policies, queries
                    Interfaces: repositories, event log, work queue, unit of work, clock
                               |                        ^
                               v                        | implemented by
   Domain           Aggregates, value objects,          |
                    domain events, invariants           |
                                                        |
   Infrastructure   BLearn.Infrastructure.Sqlite -------+
                    repositories, migrations, event log, work queue
```

A command flows like this:

1. An adapter (CLI or UI) sends a command to its handler.
2. The handler loads the aggregate through a repository.
3. The aggregate handles the command: it checks its invariants and records events, or rejects the command.
4. The handler saves through the repository. Aggregate state, new events, and any queued messages are written in **one SQLite transaction**.
5. The adapter returns the result: the emitted events, or the violated invariant.

Aggregates never call repositories. Repository interfaces belong to the application layer; SQLite classes implement them.

| Project | Contents | Depends on |
|---|---|---|
| `BLearn.Domain` | Aggregates, value objects, domain events, invariants | nothing |
| `BLearn.Application` | Command handlers, policies, queries, infrastructure interfaces | Domain |
| `BLearn.Infrastructure.Sqlite` | Repository implementations, schema migrations, event log, work queue | Application |
| `BLearn.Cli` | The `blearn` command | Application (+ Infrastructure for wiring) |
| `BLearn.Web` | Local web server and embedded UI | Application (+ Infrastructure for wiring) |

Domain tests need no database. SQLite repositories get contract tests that check them against the interface's documented behavior.

## Stores

A store is a single SQLite file, for example `union-contracts.blearn`, chosen by the user. It is the source of truth for everything in it: concepts, evidence and snapshots, events, queued work, and learner history. Stores never live in this repo.

`blearn` with no arguments starts the UI on a store picker: recent stores (remembered in the user profile), open a file, or create a new one with a name, location and starting question.

`blearn export post <folder>` writes a one-way markdown copy (one file per concept, with frontmatter and wikilinks) for reading in Obsidian or committing a snapshot to git. Edits to the export are not read back; changes go through commands so they pass the invariants.

## Domain

### Aggregates

| Aggregate | Domain operations | Invariants it enforces |
|---|---|---|
| **Graph** (one per store) | `AddEdge`, `RemoveEdge`, `AttachEvidence` | Names unique after normalization; one relationship per pair of concepts; no downward cycles; edges cite evidence |
| **Concept** | `CreateStub`, `Rename`, `ChangeGloss`, `AddAlias`, `ReplaceFacts`, `ReplaceTeaching`, `ReplaceRecallQuestions`, `RecordCheck`, requests | Gloss is one line, at most 200 characters, no links; every fact cites evidence; teaching links only to related concepts; quiz questions test existing facts; a check gives a verdict for every fact, comes from a session that captured none of the cited evidence, and flags only passages that appear in the gloss or teaching. New facts clear the check and the quiz |
| **Conversation** | `Start`, `Ask`, `Reply` | Anchored to an existing concept, fact or highlighted passage; one agent reply per learner question; replies link only to existing concepts |
| **Evidence** | `Capture`, `Verify` (or mark stale), `Reconfirm`, `Remove` | Source is registered for the store; quote appears in the snapshot; trust level transitions are valid; cited evidence cannot be removed |
| **Store settings** | `Create`, `RegisterSource`, `SetPrivate` | Root concept exists; source names unique |
| **Learner** | `RecordVisit`, `MarkKnown`, `UnmarkKnown`, `AnswerRecall` | Visits reference existing concepts |

Graph-wide invariants (no cycles, unique names) span many concepts. Rather than load the whole graph, the Graph aggregate receives a narrow read interface (for example "is there a downward path from B to A?") answered by a SQL query inside the same transaction.

### Concepts: facts and teaching

- **Gloss:** one line, shown first in tooltips.
- **Summary** and **Explanation:** teaching prose. No citations required, but they may not introduce facts absent from the Facts list. The checker agent reviews this, since it is a judgment call.
- **Facts:** atomic claims, each citing at least one piece of evidence.

A **stub** has a title and gloss only. It marks a concept that exists but has not been researched.

### Edge types

The type fixes the direction. Each edge is stored once and cites evidence.

| Type | From source | Seen from target |
|---|---|---|
| `is-a` | down | `generalizes` (up) |
| `built-on` | down | `underlies` (up) |
| `composed-of` | down | `part-of` (up) |
| `requires` | down | `required-by` (up) |
| `contrasts-with` | sideways | same |
| `related` | sideways | same |
| `created-by` | sideways | `created` |
| `implemented-in` | sideways | `implements` |

Downward edges may not form a cycle, because "deeper" must always lead somewhere new. A cycle usually means a wrong edge type or a concept that needs splitting; `implemented-in` covers bootstrapping (Roslyn is written in C#).

### Sources of truth and evidence

A source of truth is anything the harness can reach: the web, local files, or connected systems such as Airtable or Google Drive. Each store registers its sources and what each is authoritative about:

```
contracts   google-drive   Clause structure and terms of our union contracts
web         web            General labor law and public background
```

Evidence holds a registered source, a locator, a verbatim quote, and a snapshot of the full retrieved content. Code checks that the quote appears in the snapshot, ignoring differences in whitespace, case and quote characters.

| Trust level | Meaning |
|---|---|
| `fetched` | blearn fetched the source itself (public URL, local file). |
| `captured` | An agent submitted the snapshot from its harness. |
| `reconfirmed` | A different agent, in a separate session, re-fetched it and the quote matched. |

A store can require a minimum trust level per source. Re-verification marks evidence **stale** when the source no longer contains the quote. A **private** store can restrict which source systems are allowed.

## Messaging

Both channels live in the store file.

**Event log.** Every accepted command appends its domain events with an increasing sequence number. Readers each keep their own position, so everyone sees everything. The UI uses it for live updates; agents can follow it with `blearn event list --after <n> --wait`.

**Work queue.** Competing consumers: each message is delivered to exactly one agent.

- `blearn lease post <queue> --wait` blocks until a message is ready, claims it with a lease in one transaction, prints it with a receipt, and exits. SQLite's single-writer locking guarantees no two agents claim the same message.
- `blearn lease delete <receipt>` completes it; `lease patch <receipt> --release` returns it immediately. The familiar `queue receive`, `queue ack` and `queue nack` work as aliases.
- An expired lease makes the message available again. After a maximum number of attempts it moves to a dead-letter list.
- A deduplication key (for example `teach:pointer`) stops repeated requests from queuing duplicate work.
- `--wait` polls a few times a second, since SQLite cannot notify other processes. `--timeout` exits with a distinct code so the agent knows to wait again.

**Policies** connect the two: "whenever `<event>`, then `<command>`". Deterministic policies run inside blearn, in the same transaction as the triggering command (for example: whenever `TeachingRequested`, enqueue a `teach` message). Agents act as policies for work needing judgment.

| User action in the UI | Event | Queued work |
|---|---|---|
| Reach a stub, or "research this" | `ExpansionRequested` | `expand` |
| "I want to learn about this" on a fact | `TeachingRequested` | `teach` |
| Ask in the chat, highlight text and "Ask about this", or "Teach me" on a fact | `QuestionAsked` | `ask` (ahead of other work) |
| "Test me" with no quiz yet | `RecallRequested` | `recall` |
| (automatic) new facts or teaching | `FactsReplaced`, `TeachingReplaced` | `check` (behind other work) |

A released message waits a minute per attempt before redelivery, so the agent that handed it back does not receive it again at once.

## Connecting agents

**Presence.** An agent counts as connected while it waits for work (`lease post`, which polls several times a second) or holds a lease and has been active in the last 15 minutes. Finishing or releasing work and any command run with a session id also count as signs of life. Presence lives in an `agents` table, not the event log: it is operational state, not domain history. The UI receives it over the same server-sent event stream whenever it changes, shows it as a status pill, and prompts the learner to connect wherever they would otherwise wait on nobody.

**Attaching.** The learner attaches the agent they already use, with their own configuration, by pasting one line from the UI: run `blearn brief get` and follow it. Without --store, CLI commands use the store open in the running UI, so the line is the same for every store. b-learn never starts or restricts the agent itself.

**Orchestration.** The brief tells the attached agent to coordinate rather than do everything itself: claim up to four messages, give each to a subagent with its own session id, keep the receipts, start questions first, and give a check only to a subagent that did not research that concept. Separate sessions also keep the checker rule meaningful.

**Outcome check.** `lease delete` is refused until the work's result is in the store (the question has a reply, the concept is researched, the quiz or check is saved). An agent that honestly has nothing to produce, for example no source in scope, finishes with `--without-outcome "why"`, and the reason is recorded on the message.

## CLI and HTTP API

Both adapters expose the same resources with HTTP verbs. The CLI form is `blearn <resource> <verb>` and prints JSON; the web server answers the same request under `/api`. A command returns the events it emitted (exit 0, HTTP 200), or the invariant it violated (exit 1, HTTP 422).

Actions that are not edits become resources you create: a learner request, a verification, a confirmation, a lease. Events keep specific past-tense names (`FactsReplaced`, `GlossChanged`), so the log says what happened rather than "updated".

| Resource | CLI verbs | HTTP |
|---|---|---|
| store | `post`, `get`, `list` (recent), `patch --private` | `POST /stores`, `GET`/`PATCH /store` |
| source | `post`, `list` | `/sources` |
| concept | `get`, `list [--search] [--status]`, `post`, `patch` (title, gloss, aliases) | `/concepts`, `/concepts/{id}` |
| facts | `put` (replaces all facts) | `PUT /concepts/{id}/facts` |
| teaching | `put` | `PUT /concepts/{id}/teaching` |
| request | `post --kind expand|teach|recall` | `POST /concepts/{id}/requests` |
| recall-question | `put` (replaces the quiz) | `PUT /concepts/{id}/recall-questions` |
| check | `put --session` | `PUT /concepts/{id}/check` |
| question | `post [--quote] [--fact] [--conversation]` | `POST /questions` |
| reply | `post --session` | `POST /conversations/{id}/replies` |
| conversation | `get`, `list [--concept] [--waiting]` | `GET /conversations`, `/conversations/{id}` |
| page | `get --url` (the text blearn would store; saves nothing) | `GET /pages?url=` |
| neighbor, graph | `list`, `get` | `GET /concepts/{id}/neighbors`, `/graph` |
| edge | `get`, `post`, `delete` | `/edges`, `/edges/{from}/{to}` |
| edge-evidence | `post` | `POST /edges/{from}/{to}/evidence` |
| evidence | `get`, `list`, `post` (snapshot or url), `delete` (only when uncited) | `/evidence`, `/evidence/{id}` |
| snapshot | `get` | `GET /snapshots/{hash}` |
| verification, confirmation | `post` | `POST /evidence/{id}/verifications`, `/confirmations` |
| lease | `post [--wait]`, `delete` (done), `patch --release` | `POST /queues/{q}/leases`, `/leases/{receipt}` |
| message | `list [--state]` | `GET /messages` |
| event | `list [--after] [--wait]` | `GET /events`, `GET /events/stream` (server-sent events) |
| visit, known, recall-answer | `post`/`list`, `put`/`delete`/`list`, `post` | `/learner/visits`, `/learner/known/{id}`, `/learner/recall-answers` |
| import, export | `post <folder>` | `POST /imports`, `/exports` |
| brief | `get`: the agent loop, evidence rules, the store's sources and privacy policy | `GET /brief` |

Only the CLI waits (`--wait`); over HTTP a lease request on an empty queue answers 204 and the UI follows `/events/stream`.

A write is rejected if the aggregate changed since it was read (each aggregate has a version number).

## Agent roles

- **Researcher:** finds sources and captures evidence. The model's own beliefs are search leads, never evidence.
- **Writer:** writes facts only from captured evidence, and teaching only from those facts.
- **Checker:** a fresh session that sees each fact with its quotes and records a verdict per fact (supports, partial, unsupported) and flags on gloss or teaching passages no fact supports (`check put`). Code enforces a verdict for every fact, a different session from the evidence's captor, and verbatim excerpts. Whether a quote really supports a fact stays a judgment call.
- **Tutor:** answers learner questions in the conversation from the graph's facts, researching first when facts are missing.

"No source found" is a valid outcome: the claim is dropped, not filled in from memory.

## When expansion stops

- Expansion happens on demand (the user dives) or as low-priority prefetch next to where the user is.
- New concepts need evidence, so an agent cannot create one it cannot source.
- Duplicate checks run first. When most expansions resolve to existing concepts, the area is covered.
- "Nothing further in scope" is a valid result.

## UI

- **Store picker** on start.
- **Concept page:** gloss, Summary, Explanation, Facts with citation markers (hover shows the quote, source and trust level), and neighbors grouped as Deeper, Sideways and Up.
- **Inline links:** mentions of related concepts in the gloss, teaching and facts are linked automatically when the page is drawn (first mention of each, longest name wins, code untouched), so the text itself is the way to dive in. Agent replies in the chat link any concept in the store.
- **Nested tooltips:** links inside tooltips open further tooltips, each showing direction relative to the note the link sits in.
- **Chat:** a floating, minimizable window with the learner's conversations for the current concept (or all). Highlighting text anywhere on the page offers "Ask about this"; "Teach me" on a fact asks about that fact. Answers arrive live.
- **Quiz:** "Test me" walks through the concept's quiz questions, reveals each answer with the facts it tests, and records the learner's self-grade.
- **Check marks:** each fact shows the checker's verdict; unsupported teaching passages are listed above the summary.
- **Journey trail** with depth, and a "back from X" prompt when climbing up.
- **Graph view:** centered on the current concept, with up-neighbors above, down-neighbors below and sideways left and right, two or three steps out. Stubs are dashed; concepts with queued work show a loader. The layered layout relies on downward edges having no cycles.
- **Agent connection panel:** the one-line instruction to paste into a harness, queued and failed work, and the store's sources of truth with a form to add one.
- **Reaching a stub** queues its research automatically.

## Distribution

- Self-contained single-file binaries (Windows, Linux, macOS; x64 and ARM64), bundling the runtime, web server, SQLite and UI. No .NET install required.
- Install with `curl -fsSL <url>/install.sh | sh` or `irm <url>/install.ps1 | iex`.
- `npx blearn` through an npm package with per-platform binaries (the esbuild and Biome pattern).
- Release automation follows the stable and edge channel pipeline (MinVer and release-please). Details in [releasing.md](releasing.md).

## Not built yet

- **More install routes.** `dnx blearn` (a .NET tool package), Linux musl (Alpine) builds, and signed macOS and Windows binaries.
- **Minimum trust as a gate.** A source's minimum trust level marks facts as not verified in views; it does not block submission.
- **PDF and other binary sources.** `evidence fetch` handles web pages and text files; other formats must be captured as text through the harness.
- **Streaming chat.** Questions reach agents through the work queue, so answers arrive in seconds when an agent is attached, but not word by word. The recommended next step is the Agent Client Protocol (ACP): the server starts the learner's own agent (Claude Code via Zed's adapter, Codex via codex-acp, Gemini CLI, and others) over stdio, sends each chat message as a `session/prompt`, and streams `session/update` back to the browser, with b-learn's commands exposed to the agent as tools. The queue stays for research and checks.
- **Store-level policy for private stores** beyond the flag and the brief (for example, restricting which source systems may be registered).
