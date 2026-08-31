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
- Run no more than three `search` commands and no symbol-enumeration command after a useful method or location has been returned. Follow the bounded `25` -> `50` -> `200` workflow below; never run a fourth search.
- Stop after the source and focused-test evidence answers the question. A normal investigation needs one discovery search and at most a handful of follow-up reads.

## Bounded evidence workflow

For an English-oriented C# question with no declaration name:

Before the first command, turn the requested behaviors into a short evidence checklist. Each clause must be supported by an emitted implementation or focused-test location before the investigation stops; a documentation summary or a neighboring helper is only a routing hint.

1. Run one `search` with a focused natural-language query containing the central type or behavior nouns and `--max-results 25`. Let RoslynKit infer the repository, project forest, and catalog. This is normally the only search. If it has no useful method or location, refine the query and/or add `--kind method` while increasing to `--max-results 50`. If that refinement still leaves no reliable jump target, run one third and final search with `--max-results 200`. When the first result is a test method, include its containing production type in the refined query instead of switching to broad symbol enumeration.
2. Prefer returned method or test-method hits over namespace, type, and field hits. Compare `excerpt-source:` values with excerpts, kinds, identities, and locations; copy every returned `id:` or `loc:` verbatim.
3. Run `symbol-context` for the most promising returned `id:` or `loc:` before reading source when syntax structure, declaration comments, or the next semantic relation is still unclear. Use its selected symbol, ancestors, descendants, and `target-id:` values to choose one focused hop.
4. Read only the method bodies that carry the requested control-flow branches. Prefer `document-lines` around returned locations (at most 80 lines); use `symbol-source` only when a method body cannot be captured by that window. For reload/generation questions, cover both the reader/reload hand-off and the retry/mismatch branch rather than reading only the final installation helper. If the first search returns only a type, use one narrower search for its behavior in place of one source read.
5. Read at most two focused test methods, using test method IDs already returned whenever possible. Do not enumerate an entire test file or spend a command finding every related test.
6. Compare the returned evidence with the checklist. Use the final optional invocation only for one uncovered clause through a small line window or one exact test method. Use `document-symbols` only when no useful method identity is available.
7. Before stopping, account for every clause in the question, including numeric limits, wait/retry timing, precedence, and failure/reuse state when those are requested. Cite the implementation and test paths/locations already emitted, then stop. Extra searches, sibling members, and whole-file confirmation reads spend tokens without adding proof.

When a declaration identity is already known, skip discovery and call `definition`, `references`, `implementations`, or `symbol-source` with an exact emitted `id:` or a fully qualified, unambiguous name. Never substitute a shortened display name for an emitted ID. When only a simple or display name is known, resolve it with `search` or `symbols` first. For a known source location, use the position form directly. `signature-help` and mid-expression `quick-info` always require a position.

## Search and indexing

Use the stable global command from anywhere inside a standard Git repository:

```powershell
roslynkit search --query "where is configuration validated during startup" --max-results 25
```

RoslynKit finds the nearest standard `.git/` directory, discovers every tracked or unignored `.csproj`, and stores the catalog in `.roslynkit/roslynkit.db`. `search` validates and refreshes that repository partition automatically. The first request waits when no coherent partition exists; a coherent partition may answer `stale` while a writer refreshes. Use `index` for deliberate preparation and `--rebuild` only when a partition must be recreated. Pass `--target` only to narrow evaluation to a `.slnx`, `.sln`, `.slnf`, `.csproj`, or repository-directory scope; pass `--index-path` only for an advanced ignored-path override. The generated [references/commands.md](references/commands.md) file is authoritative for options and usage.

Search supports repository-local physical C# projects and non-generated source documents with one target framework. It skips source-generated documents, `bin`/`obj` paths, standard generated-code markers, external projects, and external linked non-generated files. Narrow with `--project`, `--kind`, or `--max-results` only when needed.

The semantic partition persists exact symbols, locations, comments, project references, and key type/member relationships. A fresh catalog can answer exact `symbols`, symbol-based `definition`, `symbol-source`, and `implementations` without loading an MSBuild workspace. The first exact `references` request runs Roslyn and stores its bounded result; an identical later request can use that stored result. Other position and compiler-context commands load Roslyn normally.

For a search-only judgment on a host where MSBuild workspace loading is unavailable, add `--text-only --compact --balanced`. `--text-only` scans repository C# files into a separate synthetic partition without MSBuild and cannot be combined with `--project`. `--compact` keeps only ranked declarations, repository-relative locations, and excerpts; it omits `id:` and `excerpt-source:`, so do not use it when the next step must chain into semantic navigation. `--balanced` reserves half of the bounded result set for focused tests when both production and test paths match.

```powershell
roslynkit search --query "configuration validation fallback" --max-results 25 --text-only --compact --balanced
```

`excerpt-source:` follows `excerpt:` when an excerpt is available. Its value is `documentation`, `comment`, `signature`, or `body`. An excerpt and its provenance help rank navigation candidates, but neither proves that a candidate satisfies the requested intent.

## Symbol context and metadata

A syntax node is source structure, such as an `InvocationExpression` or `MethodDeclaration`; a symbol is the compiler-resolved identity connected to that structure. `symbol-context` bridges those views from exactly one selector: `--symbol <selector>`, or `--file` with `--line --column`. Use the same emitted `id:` and `loc:` values from a search result rather than reconstructing a selector.

```powershell
roslynkit symbol-context --symbol "M:SomeNamespace.SomeType.Execute(System.String)"
```

The output identifies the selected node and symbol, alternate declarations, nearest-first syntax ancestors, and bounded descendants for declarations, invocations, constructions, and member references. Selected-node and ancestor entries carry syntax kind, location, and available `name:` or `id:` values. Descendants carry relationship, depth, syntax kind, location, and available `target-id:` values. It also returns XML documentation separately from structured declaration comments: each ordinary comment has placement (`leading`, `body`, or `trailing`), style (`line` or `block`), source location, and normalized text.

`--max-results` bounds descendants and defaults to `25`; `--max-comments` bounds ordinary comments and defaults to `3`. Each bounded collection reports its count and truncation state. Comments and XML documentation are routing hints, not proof. Use an emitted identity to select `definition`, `references`, `implementations`, `symbol-source`, or a narrow `document-lines` read for source-backed evidence.

The coding agent maintains the intent and chooses each next relationship: `search` -> `symbol-context` -> semantic navigation -> source or focused-test evidence -> decision. Track visited IDs and locations to prevent cycles, and stop when focused evidence meets the intent. RoslynKit does not embed an LLM planner or infer that a comment establishes behavior.

## Selectors and source ranges

`definition`, `references`, `implementations`, and `symbol-context` accept either `--symbol <selector>` or `--file` with `--line --column`, never both. A documentation-comment ID from output is opaque: preserve its complete prefix, containing name, and signature. Prefixes are `N:` namespace, `T:` type, `M:` method-like member (including `#ctor`), `P:` property/indexer, `F:` field, and `E:` event.

Coordinates must come from RoslynKit output, a diagnostic, or the user. If a command reports a range hint, select a fresh valid coordinate with a bounded `document-lines` call; do not retry the invalid coordinate. After source edits, line positions can be stale but symbol IDs remain the preferred chain.

For a source slice, use the smallest inclusive range:

```powershell
roslynkit document-lines --file ./src/App/Service.cs --start-line 40 --end-line 58
```

`--file` paths for RoslynKit document commands must end in `.cs`. Generated documents require `workspace --include-generated` first and the emitted generated path. Use `workspace` first only when generated/additional/analyzer-config documents or multiple document contexts are actually needed.

## Cheap semantic commands

Use `symbol-context`, `definition`, `type-definition`, `references`, `implementations`, `quick-info`, and `signature-help` for position-based navigation. Use `symbols` only for discovery when a declaration name is unknown; cap it with the smallest useful `--max-results` and prefer `--exact`/`--kind`. Use `symbol-source` for one known declaration body, never a whole class. Use `document-text` only when a complete document is genuinely required after narrower evidence failed.

Every command returns deterministic markdown. Read [references/output.md](references/output.md) for location formatting, failure shape, documentation IDs, and the shared output contract. A successful command exits `0`; failures use `error:` and `message:` lines with exit code `2`, `130`, or `1` as specified there.

## Workspace and fallback boundaries

`workspace` lists loaded projects/documents and is not a substitute for a focused source read. Add `--include-generated`, `--include-additional`, or `--include-analyzer-config` only for the requested document kind. RoslynKit is C#-only by default.

For literal search, comments/prose, or non-C# files, state that RoslynKit is not the right route for that step and let the terminal-native tool perform the bounded inspection. For a normal Roslyn workspace-load failure, use the text-only search workflow only when ranked declaration evidence is sufficient; otherwise use the terminal-native fallback. Do not shell out to editors, language servers, or a web service.
