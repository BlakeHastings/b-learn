# Releasing

b-learn ships as one self-contained binary per platform (Windows, macOS, Linux; x64 and ARM64). No .NET install is needed. The same binaries reach users three ways:

| Way | Command |
|---|---|
| npm | `npx blearn` (or `npm i -g blearn`) |
| macOS, Linux | `curl -fsSL https://raw.githubusercontent.com/BlakeHastings/b-learn/main/install/install.sh \| sh` |
| Windows | `irm https://raw.githubusercontent.com/BlakeHastings/b-learn/main/install/install.ps1 \| iex` |

## Channels

| Channel | When | Version | GitHub | npm |
|---|---|---|---|---|
| stable | merging the release-please Release PR | `0.1.0` | release `vX.Y.Z` | `latest` (`npx blearn`) |
| edge | every code change on `main` | `0.1.0-alpha.0.N` | prerelease `edge`, assets replaced | `edge` (`npx blearn@edge`) |

Versions come from git tags through MinVer and are never hardcoded (`blearn --version`). release-please decides the stable version from conventional commit titles (`feat:`, `fix:`, `feat!:`), opens a Release PR, and tags `vX.Y.Z` when it is merged. The edge tag has no `v`, so MinVer ignores it.

Installers take the edge channel with `sh -s -- --channel edge` or `$env:BLEARN_CHANNEL = 'edge'`.

## How the npm package works

`blearn` is a small launcher. Its `optionalDependencies` list one package per platform (`blearn-win32-x64`, `blearn-darwin-arm64`, ...), each declaring its `os` and `cpu`, so npm installs only the one that matches. The launcher finds it and runs the binary. `npm/build.mjs` assembles all of them from the published binaries.

## Workflows

- `build.yml` (reused): tests, publishes all six binaries from one Linux runner, names the release assets, builds and packs the npm packages, then installs them from the tarballs and runs `npx blearn` on Linux, macOS and Windows.
- `edge.yml`: on code changes to `main`, replaces the `edge` prerelease and publishes npm `edge`.
- `release.yml`: runs release-please; when a release is created, builds from the tag, attaches the assets, publishes npm `latest`.
- `ci.yml`: tests pull requests.

## Turning on npm publishing

npm publishing stays off until the repository variable `NPM_PUBLISH` is `true`.

1. Create an npm account (npmjs.com) with two-factor authentication.
2. First publish: npm trusted publishing can only be configured for a package that already exists, so the first release needs a token. Create a granular access token that can publish new packages, add it as the repository secret `NPM_TOKEN`, and set the repository variable `NPM_PUBLISH` to `true`. The next edge build publishes `blearn` and the six platform packages.
3. Then switch to tokenless publishing: on npmjs.com, for each of the seven packages, add a trusted publisher (GitHub Actions, repository `BlakeHastings/b-learn`, workflows `edge.yml` and `release.yml`). Delete the `NPM_TOKEN` secret. Publishing then uses OIDC and records provenance.

## Building locally

```sh
dotnet publish src/BLearn.Cli -c Release -r win-x64 -o dist/win-x64     # any of the six runtime identifiers
node npm/build.mjs --version "$(dist/win-x64/blearn --version)" --dist dist --out out/npm
```
