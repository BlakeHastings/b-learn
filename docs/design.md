# b-learn design

b-learn builds learning material as a graph of atomic concepts. You start at the concept your question asks about and can go **down** (what it is built on), **sideways** (related ideas at the same level) or **up** (what builds on it), as deep as you like, then come back.

This repo holds the tooling and agent skills. The knowledge itself lives in **stores** on disk, outside the repo.

## Principles

1. **Deterministic first.** Every rule that can be checked by code is checked by code (`blearn validate`). Agent instructions never act as validation.
2. **Short prose.** Agent instructions say what to produce, not lengthy justification. Over-explaining causes over-correction.
3. **Markdown is the prototype's storage.** In the target design a SQLite store file is the source of truth and markdown is a one-way export (see [architecture.md](architecture.md)).

## Store layout

```
<store>/
  blearn.yaml        # name, root (the note the question starts at)
  notes/<id>.md      # one concept per file, flat
  learner/           # journey.jsonl and, later, progress and recall results
```

## Note format

```markdown
---
id: pointer                     # kebab-case, equals the filename
title: Pointer
aliases: [pointers]             # optional; used for duplicate detection
status: complete                # stub | complete
gloss: One line, max 200 chars, no links. Shown first in tooltips.
edges:
  built-on: [memory-address]
sources: []                     # optional for now
---
## Summary

Two to four sentences. Links like [[memory-address]] or [[memory-address|an address]].

## Detail

The full explanation.
```

A **stub** has only frontmatter (no body, no edges). It marks a concept that exists but has not been researched.

## Edge types

An edge is stored once, on the source note. The type fixes the direction; the other side is computed.

| Type | Direction from source | Seen from target | Meaning |
|---|---|---|---|
| `is-a` | down | `generalizes` (up) | Source is a specific kind of target. |
| `built-on` | down | `underlies` (up) | Source is implemented on a lower layer. |
| `composed-of` | down | `part-of` (up) | Target is a component of source. |
| `requires` | down | `required-by` (up) | Understanding source needs target. |
| `contrasts-with` | sideways | same | Alternatives or opposites. |
| `related` | sideways | same | Connected at the same level. |
| `created-by` | sideways | `created` | Context: who made it. |

## Invariants (enforced by `blearn validate`)

- Frontmatter has only known fields, required fields are present, `id` is kebab-case and matches the filename.
- Ids are unique, and no id, title or alias collides with another note's (case and separators ignored).
- Gloss is one line, at most 200 characters, with no links.
- Complete notes have exactly `## Summary` then `## Detail`, both non-empty. Stubs have no body and no edges.
- Edge types are from the table above; targets exist; no self-edges.
- A pair of notes has exactly one relationship, stored on one side.
- Downward edges have no cycles: going down never brings you back.
- Every inline `[[link]]` targets an existing note **and** has an edge between the two notes, so the UI always knows its direction.
- The project root exists. Notes not connected to it are warnings.

## Next

This document describes the current prototype. The planned architecture (the Facts/Teaching split, evidence and sources of truth, the CLI resource map, jobs and waiting, private stores) is in [architecture.md](architecture.md).

- **Scale.** When parsing all markdown per request gets slow, add a derived index (for example SQLite) rebuilt from the files.
