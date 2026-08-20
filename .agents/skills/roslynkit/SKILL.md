---
name: roslynkit
description: Use the stable global RoslynKit tool first for ordinary C# semantic inspection; for literal search, prose, non-C# files, or RoslynKit workspace-load failures, use the terminal-native fallback.
---

# RoslynKit

RoslynKit is a compact, read-only semantic navigator for C# declarations, source locations, and compiler-backed relationships. Use it for symbol questions; use the terminal-native tool for prose, literal text, non-C# files, or a workspace-load failure.

## Non-negotiable limits

- Treat the investigation budget as hard: use at most 8 RoslynKit invocations total, including discovery and source/test reads; aim to finish in six. Stop and synthesize once the requested implementation and focused-test evidence is complete.
- Never read an entire C# file or type when a declaration or position command can answer the question.
- Never read the same file twice. Capture the useful source and test evidence on the first pass.
- Never use `symbol-source` on a namespace or type. Select a method, property, field, or event instead.
- Never request more than 80 inclusive lines with `document-lines`; choose the smallest window around the relevant location.
- Never use `document-symbols` merely to turn a known declaration identity into coordinates. Use its fully qualified, unambiguous name or emitted `id:` directly.
- When output provides an `id:`, pass that exact documentation-comment ID to the next `--symbol` command. Never shorten it or replace it with the adjacent display name.
- Once the RoslynKit route starts, do not read C# source with `rg`, `grep`, `sed`, `cat`, or another shell text command. Use one bounded RoslynKit source or line-window command per implementation/test file instead.
- Run no more than two `search` commands and no symbol-enumeration command after a useful method or location has been returned. Spend remaining calls on the returned production and test windows, never on a third search.
- Stop after the source and focused-test evidence answers the question. A normal investigation needs one discovery search and at most a handful of follow-up reads.

## Bounded evidence workflow

For an English-oriented C# question with no declaration name:

Before the first command, turn the requested behaviors into a short evidence checklist. Each clause must be supported by an emitted implementation or focused-test location before the investigation stops; a documentation summary or a neighboring helper is only a routing hint.

1. Run one `search` with `--target`, `--index-path`, a focused natural-language query containing the central type or behavior nouns, and `--max-results 5`. This is normally the only search; use one narrower second search only when the first result set has no relevant implementation or test method. When the first result is a test method, include its containing production type in that narrower query instead of switching to broad symbol enumeration.
2. Prefer returned method or test-method hits over namespace, type, and field hits. Copy every returned `id:` or `loc:` verbatim.
3. Read only the method bodies that carry the requested control-flow branches. Prefer `document-lines` around returned locations (at most 80 lines); use `symbol-source` only when a method body cannot be captured by that window. For reload/generation questions, cover both the reader/reload hand-off and the retry/mismatch branch rather than reading only the final installation helper. If the first search returns only a type, use one narrower search for its behavior in place of one source read.
4. Read at most two focused test methods, using test method IDs already returned whenever possible. Do not enumerate an entire test file or spend a command finding every related test.
5. Compare the returned evidence with the checklist. Use the final optional invocation only for one uncovered clause through a small line window or one exact test method. Use `document-symbols` only when no useful method identity is available.
6. Before stopping, account for every clause in the question, including numeric limits, wait/retry timing, precedence, and failure/reuse state when those are requested. Cite the implementation and test paths/locations already emitted, then stop. Extra searches, sibling members, and whole-file confirmation reads spend tokens without adding proof.

When a declaration identity is already known, skip discovery and call `definition`, `references`, `implementations`, or `symbol-source` with an exact emitted `id:` or a fully qualified, unambiguous name. Never substitute a shortened display name for an emitted ID. When only a simple or display name is known, resolve it with `search` or `symbols` first. For a known source location, use the position form directly. `signature-help` and mid-expression `quick-info` always require a position.

## Search and indexing

Use the stable global command and always pass an explicit target:

```powershell
roslynkit search --target ./SomeSolution.slnx --index-path ./artifacts/roslynkit.db --query "workspace daemon reload after source changes" --max-results 5
```

`search` validates and refreshes the selected target automatically. The first request waits when no coherent partition exists; a coherent partition may answer `stale` while a writer refreshes. Use `index` for deliberate preparation and `--rebuild` only when a target partition must be recreated. The generated [references/commands.md](references/commands.md) file is authoritative for options and usage.

Search supports repository-local physical C# projects and non-generated source documents with one target framework. It skips source-generated documents, `bin`/`obj` paths, standard generated-code markers, external projects, and external linked non-generated files. Narrow with `--project`, `--kind`, or `--max-results` only when needed.

## Selectors and source ranges

`definition`, `references`, and `implementations` accept either `--symbol <selector>` or `--file` with `--line --column`, never both. A documentation-comment ID from output is opaque: preserve its complete prefix, containing name, and signature. Prefixes are `N:` namespace, `T:` type, `M:` method-like member (including `#ctor`), `P:` property/indexer, `F:` field, and `E:` event.

Coordinates must come from RoslynKit output, a diagnostic, or the user. If a command reports a range hint, select a fresh valid coordinate with a bounded `document-lines` call; do not retry the invalid coordinate. After source edits, line positions can be stale but symbol IDs remain the preferred chain.

For a source slice, use the smallest inclusive range:

```powershell
roslynkit document-lines --target ./SomeSolution.slnx --file ./src/App/Service.cs --start-line 40 --end-line 58
```

`--file` paths for RoslynKit document commands must end in `.cs`. Generated documents require `workspace --include-generated` first and the emitted generated path. Use `workspace` first only when generated/additional/analyzer-config documents or multiple document contexts are actually needed.

## Cheap semantic commands

Use `definition`, `type-definition`, `references`, `implementations`, `quick-info`, and `signature-help` for position-based navigation. Use `symbols` only for discovery when a declaration name is unknown; cap it with the smallest useful `--max-results` and prefer `--exact`/`--kind`. Use `symbol-source` for one known declaration body, never a whole class. Use `document-text` only when a complete document is genuinely required after narrower evidence failed.

Every command returns deterministic markdown. Read [references/output.md](references/output.md) for location formatting, failure shape, documentation IDs, and the shared output contract. A successful command exits `0`; failures use `error:` and `message:` lines with exit code `2`, `130`, or `1` as specified there.

## Workspace and fallback boundaries

`workspace` lists loaded projects/documents and is not a substitute for a focused source read. Add `--include-generated`, `--include-additional`, or `--include-analyzer-config` only for the requested document kind. RoslynKit is C#-only by default.

For literal search, comments/prose, non-C# files, or RoslynKit workspace-load failures, state that RoslynKit is not the right route for that step and let the terminal-native tool perform the bounded inspection. Do not shell out to editors, language servers, or a web service.
