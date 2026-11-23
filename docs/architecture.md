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

`blearn export markdown <folder>` writes a one-way markdown copy (one file per concept, with frontmatter and wikilinks) for reading in Obsidian or committing a snapshot to git. Edits to the export are not read back; changes go through commands so they pass the invariants.

## Domain

### Aggregates

| Aggregate | Example commands | Invariants it enforces |
|---|---|---|
| **Graph** (one per store) | `CreateStub`, `AddEdge`, `RemoveEdge`, `RenameConcept` | Names unique after normalization; one relationship per pair of concepts; no downward cycles; edges cite evidence |
| **Concept** | `SubmitFacts`, `SubmitTeaching`, `AddAlias` | Gloss is one line, at most 200 characters, no links; every fact cites evidence; teaching links only to related concepts |
| **Evidence** | `CaptureEvidence`, `VerifyEvidence`, `ReconfirmEvidence`, `MarkStale` | Source is registered for the store; quote appears in the snapshot; trust level transitions are valid |
| **Store settings** | `RegisterSource`, `SetPrivate`, `SetRoot` | Root concept exists; source names unique |
| **Learner** | `RecordVisit`, `MarkKnown`, `SubmitRecall` | Visits reference existing concepts |

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

**Event log.** Every accepted command appends its domain events with an increasing sequence number. Readers each keep their own position, so everyone sees everything. The UI uses it for live updates; agents can follow it with `blearn events wait --after <n>`.

**Work queue.** Competing consumers: each message is delivered to exactly one agent.

- `blearn queue receive <queue> --wait` blocks until a message is ready, claims it with a lease in one transaction, prints it with a receipt, and exits. SQLite's single-writer locking guarantees no two agents claim the same message.
- `blearn queue ack <receipt>` completes it; `nack` returns it immediately.
- An expired lease makes the message available again. After a maximum number of attempts it moves to a dead-letter list.
- A deduplication key (for example `teach:pointer`) stops repeated requests from queuing duplicate work.
- `--wait` polls a few times a second, since SQLite cannot notify other processes. `--timeout` exits with a distinct code so the agent knows to wait again.

**Policies** connect the two: "whenever `<event>`, then `<command>`". Deterministic policies run inside blearn, in the same transaction as the triggering command (for example: whenever `TeachingRequested`, enqueue a `teach` message). Agents act as policies for work needing judgment.

| User action in the UI | Event | Queued work |
|---|---|---|
| Reach a stub, or "research this" | `ExpansionRequested` | `expand` |
| "I want to learn about this" on a fact | `TeachingRequested` | `teach` |
| Ask a question on a concept | `QuestionAsked` | `ask` |
| "Test me" | `RecallRequested` | `recall` |

## CLI

Commands follow `blearn <resource> <action>` and print JSON. A command returns the events it emitted, or the invariant it violated with a non-zero exit code.

| Resource | Actions |
|---|---|
| `store` | `create`, `info`, `recent`, `export` |
| `source` | `register`, `list` |
| `concept` | `get`, `list`, `search`, `stub`, `submit-facts`, `submit-teaching`, `rename` |
| `edge` | `add`, `remove` |
| `evidence` | `capture`, `fetch`, `verify`, `reconfirm` |
| `queue` | `receive --wait`, `ack`, `nack`, `list`, `dead-letters` |
| `events` | `list`, `wait --after <n>` |
| `learner` | `journey`, `known`, `recall` |
| `agent` | `brief`: prints the agent loop, evidence rules, the store's sources and privacy policy |

A write is rejected if the aggregate changed since it was read (each aggregate has a version number).

## Agent roles

- **Researcher:** finds sources and captures evidence. The model's own beliefs are search leads, never evidence.
- **Writer:** writes facts only from captured evidence, and teaching only from those facts.
- **Checker:** a fresh agent that sees each fact with its quote, and the teaching text with the Facts list. It records whether each quote supports its fact, flags teaching that adds unsupported facts, and flags evidence used outside its source's scope.

"No source found" is a valid outcome: the claim is dropped, not filled in from memory.

## When expansion stops

- Expansion happens on demand (the user dives) or as low-priority prefetch next to where the user is.
- New concepts need evidence, so an agent cannot create one it cannot source.
- Duplicate checks run first. When most expansions resolve to existing concepts, the area is covered.
- "Nothing further in scope" is a valid result.

## UI

- **Store picker** on start.
- **Concept page:** gloss, Summary, Explanation, Facts with citation markers (hover shows the quote, source and trust level), and neighbors grouped as Deeper, Sideways and Up.
- **Nested tooltips:** links inside tooltips open further tooltips, each showing direction relative to the note the link sits in.
- **Journey trail** with depth, and a "back from X" prompt when climbing up.
- **Graph view:** centered on the current concept, with up-neighbors above, down-neighbors below and sideways left and right, two or three steps out. Stubs are dashed; concepts with queued work show a loader. The layered layout relies on downward edges having no cycles.
- **Agent connection panel:** the one-line instruction to paste into a harness, and which agents are currently receiving work.

## Distribution

- Self-contained single-file binaries (Windows, Linux, macOS; x64 and ARM64), bundling the runtime, web server, SQLite and UI. No .NET install required.
- Install with `curl -fsSL <url>/install.sh | sh` or `irm <url>/install.ps1 | iex`.
- `npx blearn` through an npm package with per-platform binaries (the esbuild and Biome pattern).
- `dnx blearn` for users with the .NET 10 SDK.
- Release automation follows the stable and edge channel pipeline (MinVer and release-please).

## Not built yet

- **Distribution.** Self-contained binaries, install scripts, the npm wrapper and release automation. Today it runs with the .NET SDK.
- **Checker verdicts.** The checker role is defined, but there is no command yet to record its per-citation verdict (supports / partial / does not support) or its flags on teaching text.
- **Minimum trust as a gate.** A source's minimum trust level marks facts as not verified in views; it does not block submission.
- **PDF and other binary sources.** `evidence fetch` handles web pages and text files; other formats must be captured as text through the harness.
- **Recall flow.** `recall` requests queue work and answers can be recorded, but there is no quiz UI yet.
- **Store-level policy for private stores** beyond the flag and the brief (for example, restricting which source systems may be registered).
