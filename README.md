# b-learn

Tools and agent skills for learning any topic as a graph of atomic concepts you can dive into and climb back out of.

See [docs/design.md](docs/design.md) for the model, note format, edge types and invariants.

## Usage

Requires the .NET 10 SDK.

```sh
# check a store against every invariant (exit code 1 on errors)
dotnet run --project src/BLearn.Cli -- validate <store-path>

# open the local learning UI at http://localhost:5170
dotnet run --project src/BLearn.Cli -- serve <store-path> [--port N]

# run the validator tests
dotnet test
```

## Layout

- `src/BLearn.Core`: note parsing, the graph, and the validator.
- `src/BLearn.Cli`: the `blearn` command (`validate`, `serve`) and the prototype UI (`index.html`).
- `tests/BLearn.Core.Tests`: one test per invariant.
