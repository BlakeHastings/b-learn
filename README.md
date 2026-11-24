# b-learn

Learn any topic as a graph of atomic concepts you can dive into and climb back out of. Every fact and relationship cites evidence from a source of truth; what a model believes is a lead, never proof.

The design is in [docs/architecture.md](docs/architecture.md).

## Quick start

Requires the .NET 10 SDK for now (self-contained binaries are planned).

```sh
dotnet run --project src/BLearn.Cli          # opens the web UI on the store picker
```

Create a store from the picker, or from the command line:

```sh
blearn store post ~/Documents/b-learn/rust.blearn --name "Learning Rust" \
  --root-title Rust --root-gloss "A systems programming language focused on safety and speed."
blearn --store ~/Documents/b-learn/rust.blearn source post web --system web \
  --scope "Official documentation and encyclopedias"
blearn --store ~/Documents/b-learn/rust.blearn serve
```

## Working with an agent

Attach the agent you already use (Claude Code, Codex, Cursor, Gemini CLI, or any agent that can run shell commands): copy the line the UI offers (in the Agents panel, or wherever it says no agent is connected) and paste it into your agent. It reads `blearn brief get`, coordinates subagents to work on several requests at once, and the UI shows it as connected while it listens.

## CLI

Commands are `blearn <resource> <verb>` with the HTTP verbs `get`, `list`, `post`, `put`, `patch` and `delete`, and the web UI serves the same resources under `/api`. For example `blearn concept patch pointer --gloss "..."` is `PATCH /api/concepts/pointer`. `blearn help` lists every command. All commands print JSON. Exit codes: 0 ok, 1 rejected by a rule (the output names the rule), 2 usage, 3 wait timed out, 4 not found.

## Layout

| Project | Role |
|---|---|
| `src/BLearn.Domain` | Aggregates, value objects, domain events, invariants |
| `src/BLearn.Application` | Command handlers, policies, read models, ports |
| `src/BLearn.Infrastructure.Sqlite` | Repositories, schema, event log, work queue |
| `src/BLearn.Infrastructure.Markdown` | Legacy import and one-way markdown export |
| `src/BLearn.Infrastructure.Fetching` | Fetching web pages and local files for evidence |
| `src/BLearn.Web` | Local web UI (embedded page and HTTP API) |
| `src/BLearn.Cli` | The `blearn` command, which also hosts the UI |
| `tests/` | Domain tests (no database) and integration tests (real SQLite files) |

```sh
dotnet test
```

## License

GPL-3.0. See [LICENSE](LICENSE).
