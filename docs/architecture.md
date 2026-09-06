# RoslynKit retained-workspace architecture

Status: accepted design with an initial implementation. Decisions 1–19 below are the design authority for this rewrite. Existing command-line-only documentation is migration context, not a constraint on the new architecture. Windows execution and large-repository performance still require validation.

## Purpose

Keep Roslyn syntax trees, compilations, and project state alive across related requests. Persist only rebuildable search data. A saved source edit should normally update a document in an existing immutable Roslyn solution, not load every project again.

RoslynKit remains a read-only analysis tool. Project evaluation and package restore can execute build logic with the process's permissions; read-only analysis is not an execution sandbox.

## Accepted decisions

| Decision | Policy |
| --- | --- |
| 1. Ownership | The Model Context Protocol (MCP) client owns a long-lived RoslynKit server process. No independently discoverable daemon. Load workspaces lazily and retain them across requests. |
| 2. Transport | Client-to-server communication uses standard input and standard output (stdio). Expose exactly two MCP tools: `help` and `query`. |
| 3. Platforms | Windows and Linux are priorities. Internal Roslyn/MSBuild named pipes are permitted. A sandbox that prevents those mechanisms is not a reason to replace the standard project loader. |
| 4. Projects | Modern .NET C# projects, including repositories containing only `.csproj` files. Solution files are optional. Legacy .NET Framework is excluded. Platform-specific workloads remain subject to the installed host capabilities. |
| 5. Persistence | Keep repository-local SQLite Full-Text Search 5 (FTS5) indexes using Best Matching 25 (BM25) ranking, plus search navigation and invalidation metadata. Do not cache completed query answers, on disk or in an application-level answer cache. Roslyn's own reuse remains enabled. |
| 6. Semantics | No separate persistent semantic catalog. Resolve exact symbols, references, implementations, and compiler context from live Roslyn snapshots. |
| 7. Command line | Retain a short-lived command-line interface (CLI) using the same execution engine. Cold semantic startup is acceptable here. A separate CLI invocation does not discover or control a private MCP process. |
| 8. Detection | Use `System.IO.FileSystemWatcher`, coalesced updates, periodic background reconciliation, and overflow/error recovery. Apply already-observed changes before new analysis. No unconditional whole-input scan before an ordinary query. Occasional full background content checks and detection delay are accepted. |
| 9. Updates | Incrementally apply text changes to existing C# documents. Initially replace the loaded workspace for additions, deletions, renames, project membership, configuration, reference, and other structural changes. A new class inside an existing file is a text edit. |
| 10. Handover | Queries capture immutable snapshots. Existing readers finish on the old snapshot. New workspace-dependent queries wait for known refresh work. Publish a successful replacement atomically; report failure instead of silently serving stale analysis. Dispose retired owners after their readers finish. |
| 11. Indexing | Update indexes in the background, independently of semantic availability. Search waits for an index known to be outdated; semantic queries do not wait for indexing. Detection delay still applies. |
| 12. Retention | Bound retained scopes; evict least-recently-used idle scopes and stop their watchers. Never evict a scope still in use. Returning to an evicted scope reloads Roslyn and checks persisted index compatibility. This is a count bound, not a strict memory limit. |
| 13. Repository | Every `query` supplies an explicit absolute repository root. The caller selects it; RoslynKit validates, normalizes, and routes it. Results identify the resolved root. Cross-drive access is supported within the server's filesystem namespace. |
| 14. Frameworks | A supported project targets one framework. Reject multi-targeted projects; do not silently select a target. Different projects and repositories can use different supported target versions. |
| 15. Restore/build | Automatically restore missing or outdated dependencies when needed, with an opt-out. Do not restore on every query or ordinary source edit. Full builds remain user/agent initiated. After a build attempt completes, the agent requests and awaits MCP synchronization before subsequent analysis, including after failed builds. No MSBuild hook initially. |
| 16. Repository access | No RoslynKit trust registry, allowlist, or repository approval gate. Normal path validation and host permissions still apply. Repository selection does not confine executed build logic to that directory. |
| 17. SDK | Resolve the installed .NET Software Development Kit (SDK) for each repository using normal selection rules, including `global.json`. No automatic SDK installation. Missing/unsupported SDKs produce actionable errors. |
| 18. Multiple clients | Simultaneous local clients keep separate live workspaces and share compatible search partitions in the same repository database. Coordinate writers and reject obsolete publication. Recover when a writer exits. Shared databases on network filesystems are out of scope. |
| 19. Git | Require an ordinary Git checkout with a `.git` directory. Non-Git repositories, linked worktrees, and other `.git` indirection layouts are unsupported initially. Git-visible projects are discovery inputs, not a complete list of compiler inputs. |

## Process and ownership model

```text
MCP client
  └─ RoslynKit stdio server: help, query; bounded scope routing
       ├─ repository/scope worker: installed SDK A
       │    ├─ live workspace owner → immutable solution snapshots → reader leases
       │    ├─ file watcher + input manifest + reconciliation
       │    └─ background search publisher → repository A/.roslynkit/roslynkit.db
       └─ repository/scope worker: installed SDK B
            └─ independent live state → repository B/.roslynkit/roslynkit.db
```

Workers are a server-owned isolation mechanism, not an independent daemon or another public endpoint. A private stdio connection avoids a discoverable socket or named-pipe service. Each worker starts in its repository directory; the shared server does not change its process-wide working directory to route requests. Repository-specific worker isolation prevents process-global MSBuild registration from silently binding another repository to the first SDK.

The initial retention default is four scopes, configurable at server startup. The limit includes workers being loaded or used. Closing the client connection shuts down owned workers. Cancellation of one query must not dispose state still used by another query.

## Snapshot and freshness model

A Roslyn `Workspace` is an analysis container; its `Solution` is a logical immutable snapshot containing projects and documents. It does not require a solution file on disk. Multiple physical project contexts may map one file to multiple document identities.

The runtime keeps workspace ownership separate from snapshot views. Applying saved source text creates a new solution snapshot using Roslyn APIs such as `WithDocumentText`; it does not call a workspace apply API that could write files. Linked document contexts all receive the update. Captured older solutions remain usable by existing readers.

A scope tracks a monotonically increasing local revision and a content-based input identity. The local revision coordinates readers and background work inside one worker; it is not a cross-process ordering token. The content identity and compatible scope/settings identify persisted search data.

Watch events are hints, not a complete change log. Reconciliation compares saved inputs, discovers membership changes, and repairs missed events. The manifest includes evaluated project inputs, source and additional files, analyzer configuration, relevant references and restore assets, rather than treating Git status as a complete compiler input list. Errors and watcher overflow request broader reconciliation. Explicit `query` synchronization performs a stronger saved-input check; this cost is not imposed on every semantic query.

When replacement fails, the old owner stays alive only for existing readers and recovery. New analysis reports the failure until a later change or explicit synchronization succeeds. Concurrent editing cannot provide an atomic filesystem snapshot; published state is a coherent captured Roslyn snapshot subject to the accepted detection policy.

## Search publication

Search indexes store declaration retrieval fields, exact navigation identities when available, paths, locations, excerpts, and scope/input metadata. Exact semantic answers come from Roslyn, never from a parallel symbol/relationship catalog or serialized answer cache.

Use SQLite write-ahead logging for same-machine readers and writers. Hold the coordinated writer lease before constructing a corpus; validate its captured inputs before publication. A process-local revision alone cannot establish freshness against another client's snapshot. A changed or superseded input identity aborts publication and requests synchronization. A transaction publishes index rows and metadata together, and rollback releases failed work.

Keep the last coherent committed index while work runs, but do not return it as fresh when the requesting worker knows relevant inputs changed. Search waits for the required revision; semantic queries continue once workspace synchronization finishes. Reuse across restart requires matching schema, scope/settings, and input identity.

Old semantic-catalog and answer-cache tables are rebuildable application data. Migrate the schema explicitly, remove these obsolete tables, and rebuild incompatible search partitions. Do not delete repository source or unrelated files.

## Public workflow

Start the server with `roslynkit serve`. `--max-workspaces` configures retained scopes; `--no-restore` disables automatic restore. Protocol output alone goes to stdout; diagnostics go to stderr.

The `help` tool provides operation discovery on demand. The `query` tool takes `repositoryRoot` and an `args` array. Relative path options resolve from that explicit root, not the client's initial working directory. Ordinary semantic and search operations retain their existing CLI argument shapes and Markdown payloads.

`refresh` is a logical operation of `query`, not a third MCP tool or a standalone CLI command. It reconciles saved inputs and waits for workspace synchronization. It does not run a full build and does not necessarily replace the workspace. Index readiness remains separate; a later search waits when necessary.

After an agent-initiated `dotnet build` attempt completes, the agent awaits `query` with `args: ["refresh"]` for the same repository/scope. Watchers and reconciliation remain necessary for human builds and omitted notifications. No automatic hook or trust prompt is introduced.

## Acceptance and validation

- Protocol: initialize, discover exactly two tools, bounded help, valid/invalid query arguments, cancellation, stdout isolation, disconnect cleanup, and real subprocess exchange.
- Reuse: a second semantic query uses the same owner; text edits retain unchanged Roslyn state; old leases remain unchanged and usable.
- Updates: linked document identities, add/delete/rename, external evaluated inputs, additional files, analyzer configuration, generator/reference inputs, overflow recovery, and periodic missed-event repair.
- Handover: known refresh blocks new readers, replacement failure is visible, successful recovery publishes once, and old owners are disposed only after readers release them.
- Search: known-outdated search waits, semantic queries do not wait for the index, obsolete publishers cannot overwrite newer inputs, process failure releases ownership, and migration removes semantic/answer-cache persistence.
- Scope: explicit absolute roots, independent repositories and drives, bounded idle eviction, ordinary Git checkout requirements, and clear unsupported-layout errors.
- Toolchains: two repository SDK selections in the same client lifetime, missing SDK errors without installation, single-target validation including imported properties, automatic/disabled restore, no ordinary-source restore, and no implicit full build.
- Compatibility: existing semantic command/output tests, generated command reference, package build, and standalone CLI behavior.
- Platforms: run the same functional tests and process smoke tests on Windows and Linux. Linux results alone do not establish Windows support.
- Performance: record cold load, warm semantic queries, source-edit synchronization, structural replacement, search catch-up, and retained memory separately. Do not present unmeasured IDE-like latency as established.

Implementation tests and platform results, rather than this design's acceptance status, determine which guarantees are ready for release.

## Initial implementation tradeoffs

- Ordinary warm semantic queries do not scan all inputs. Background reconciliation, explicit synchronization, and index publication validation can read and hash the full manifest. Index publication currently validates before building and before committing; this is conservative cross-process correctness work, not an established large-repository performance result.
- Discovery currently covers more files than the evaluated compiler input set. Unrelated repository changes can therefore cause unnecessary workspace replacement. Known RoslynKit database outputs are excluded explicitly to prevent self-invalidation.
- Known external inputs remain in reconciliation even when the external watcher limit of 128 directories is reached. A new external wildcard input outside all discovered directories requires structural reevaluation to become known.
- Scope retention is bounded by count. Immutable metadata images and overlapping workspace generations can consume substantial memory; no hard process memory budget is enforced.
- The checked-in continuous integration matrix covers Windows and Linux with two installed SDK versions. Local validation in this implementation session runs on Linux; Windows results and representative cold/warm/edit/search memory and latency measurements remain release work.
