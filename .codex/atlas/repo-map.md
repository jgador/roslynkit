# RoslynKit Map

Resident architecture context for first-pass navigation. Atlas stores durable routing facts only; use `git ls-files`, `rg`, RoslynKit live commands, tests, and direct file reads for current facts.

## Shape

- Solution: `RoslynKit.slnx`
- Product: `src/RoslynKit/`
- Tests: `tests/RoslynKit.Tests/`
- Test utility: `tests/RoslynKit.WorkspaceGraphDump/`
- Fixture input: `tests/FixtureWorkspace/App/`
- Docs and packaging: [README.md](../../README.md), [docs/](../../docs/), [docs/daemon.md](../../docs/daemon.md), [docs/agents/](../../docs/agents/), `scripts/`
- Agent assets: [AGENTS.md](../../AGENTS.md), [.agents/skills/roslynkit/](../../.agents/skills/roslynkit/), [.agents/skills/roslynkit/references/commands.md](../../.agents/skills/roslynkit/references/commands.md), [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md), [.agents/skills/roslynkit-dev/SKILL.md](../../.agents/skills/roslynkit-dev/SKILL.md), [.agents/skills/benchmark/SKILL.md](../../.agents/skills/benchmark/SKILL.md), [.agents/skills/grill-me/SKILL.md](../../.agents/skills/grill-me/SKILL.md), `.agents/skills/commit-context/`, `.agents/skills/git-commit-push/`, [.codex/atlas/repo-map.md](repo-map.md)

## Runtime Flow

- `Program.Main` routes the exact hidden daemon token through `DaemonServerRunner` before public parsing; `Program.CreateCliApplication` creates the ordinary `CliApplication` with separate stdout and stderr writers and injects `DaemonFallbackWorkspaceCommandRouter` for workspace commands.
- `CliApplication.ExecuteAsync` parses args and returns an exact buffered `CliProcessResult`; `RunAsync` writes that result to the configured process streams.
- Help, version, and init execute entirely locally. Daemon lifecycle controls parse locally and use a non-starting client exchange; workspace-backed commands cross the injected `DaemonFallbackWorkspaceCommandRouter` boundary.
- `DaemonFallbackWorkspaceCommandRouter` delegates workspace commands to `DaemonClient`, falls back only on its typed daemon infrastructure failure, prefixes buffered stderr with one exact warning, and invokes `WorkspaceCommandRouter` once. `WorkspaceCommandRouter` supplies the standalone function, calls `RoslynCommandExecutor.ExecuteAsync`, and renders through `MarkdownProjection` without writing directly to process streams.
- `CliParser` resolves the longest registered command-token path, then validates selector/option combinations against `BuiltinCommandRegistry`; this supports leaf commands such as `daemon status` while preserving canonical string command names.
- `DaemonCommandExecutor` handles `daemon status` and `daemon stop` through `DaemonClient` without loading a workspace or starting a daemon; absence reports `state: not-running`, running status renders the host snapshot, and an acknowledged stop reports `state: stopping`.
- `GitWorkspaceIdentityResolver` validates the committed Git worktree boundary and captures the target, `global.json`, SDK/MSBuild, build environment, RoslynKit build, protocol, and architecture compatibility inputs. Both `DaemonClient` and `DaemonServerRunner` consume the same resolution before deriving the endpoint.
- `DaemonIdentityResolver` adds the Windows SID or Unix effective UID and canonical local IPC runtime directory, while `DaemonEndpointName` canonicalizes the complete compatibility identity and emits a fixed opaque SHA-256 endpoint name. `DaemonServerRunner` uses the endpoint for the lifetime lease and listener.
- `DaemonProtocol` reads and writes bounded four-byte little-endian strict UTF-8 JSON frames over any stream, while typed handshake, command, status, and stop messages carry protocol versions and request IDs. `DaemonNamedPipe` supplies local asynchronous current-user-only byte-mode streams consumed by `WorkspaceDaemonServer`.
- `PathCanonicalizer` is the shared reparse-point-aware absolute path boundary used by Git workspace identity and daemon command path options.
- `GitWorktreeFingerprintService` performs the request-time stable `HEAD` / raw porcelain / per-file `hash-object --no-filters` capture, coalesces concurrent callers, and returns deterministic structural fingerprints or typed cache-reuse failures.
- `WorkspaceDaemonSession` owns disposable workspace generations, reconciles fingerprints before leasing an immutable snapshot, drains active readers before full reload, admits at most three clean readers, gives pending reloads priority, and performs the single quiet-period retry. The hidden server owns it through `WorkspaceDaemonHost`; public workspace commands reach it through `DaemonClient`.
- `WorkspaceDaemonHost` is the transport-independent lifecycle owner around one session. `WorkspaceDaemonServer` adds the bounded concurrent named-pipe accept loop, required handshake, single-operation dispatch, target revalidation, and prompt disconnect cancellation. `DaemonServerRunner` resolves the hidden process identity, holds the per-endpoint lifetime lease, and constructs the server.
- `DaemonPipeClient` owns connect, handshake, single-operation exchange, and response correlation. `DaemonClient` resolves endpoints, normalizes command-path infrastructure and protocol failures into the typed fallback signal, sends directly to running servers, or uses the distinct `DaemonBootstrapLease` to serialize recheck, `DaemonProcessStarter` self-launch, and handshake readiness. The Windows starter mirrors Roslyn's compiler-server `CreateProcess` boundary with no window, no inherited handles, and invalid standard handles; other platforms use non-waiting `Process.Start` with redirected streams. `DaemonFallbackWorkspaceCommandRouter` owns the strict typed fallback boundary, buffered-stderr warning, and one standalone retry. Public lifecycle commands reuse the exchange without startup and never use fallback.
- `ProcessCommandRunner` is the argument-list-only, buffered child-process boundary used by workspace identity and fingerprint probes; its byte-output path preserves NUL-delimited Git status records without invoking a shell.
- `RoslynCommandExecutor.ExecuteAsync(command, cancellationToken)` owns standalone workspace loading and delegates to the caller-owned workspace overload; the overload resolves documents or symbols, invokes Roslyn APIs, and returns result models without disposing the workspace.
- `SearchCommandService` coordinates the `index` and `search` commands around an explicit repository-local SQLite index path. The database stores separate target partitions, repository-relative target identities, project paths, and declaration source paths, full-text search with internal Best Matching 25 ranking, and write-ahead logging; public target and declaration locations are reconstructed as absolute paths from the resolved repository root. It is independent of daemon endpoint identity. `search` validates and incrementally refreshes a target automatically; `index` is the strict explicit refresh boundary and `--rebuild` forces that target's full rebuild. A prior coherent partition may answer concurrent searches as `stale` while a refresh runs.
- `MarkdownProjection` is the deterministic markdown output renderer for successful commands.

```mermaid
flowchart TD
    A["Command-line args<br/>string[] args"] --> B["Program.Main<br/>src/RoslynKit/Program.cs"]
    B --> BM{"Hidden daemon token?"}
    BM -->|No| C["CliApplication.RunAsync<br/>write buffered stdout + stderr"]
    BM -->|Yes| DSR["DaemonServerRunner.RunAsync<br/>resolve identity + lifetime lease"]
    DSR --> DSS["WorkspaceDaemonServer.RunAsync<br/>handshake + one-operation pipe dispatch"]
    DSS --> DSH["WorkspaceDaemonHost<br/>session lifecycle + idle/stop"]
    C --> C1["CliApplication.ExecuteAsync<br/>src/RoslynKit/CliApplication.cs"]

    C1 --> D["CliParser.Parse(args)<br/>src/RoslynKit/CliParser.cs"]
    D --> E["BuiltinCommandRegistry<br/>command metadata, options, usage"]
    E --> F["ParsedCommand<br/>name + options + selectors"]

    F --> G{"Top-level route"}
    G -->|help| H["MarkdownProjection.RenderHelp<br/>help overview or command help"]
    G -->|version| I["VersionText"]
    G -->|init| IA["InitCommandExecutor.Execute<br/>embedded skill-bundle scaffold"]
    G -->|daemon status / daemon stop| DC["DaemonCommandExecutor.ExecuteAsync<br/>non-starting lifecycle control"]
    G -->|semantic command| JR["Injected workspace-command function"]
    DC --> DCL["DaemonClient<br/>identity + pipe exchange"]
    JR --> DCL
    DCL -->|absent workspace command| DBL["DaemonBootstrapLease<br/>recheck + self-start"]
    DBL --> DPS["DaemonProcessStarter<br/>same apphost or dotnet + DLL"]
    DCL --> DSS
    DPS --> DSR
    DSH --> R["WorkspaceDaemonSession<br/>load or reuse generation"]
    R --> J2["RoslynCommandExecutor.ExecuteAsync<br/>caller-owned workspace overload"]
    J2 --> K{"Command dispatch switch"}
    K --> L["workspace / diagnostics"]
    K --> M["symbols / document-symbols"]
    K --> N["definition / type-definition"]
    K --> O["references / implementations"]
    K --> P["quick-info / signature-help"]
    K --> Q["document-text / document-lines / symbol-source"]

    L --> S{"Resolution path"}
    M --> S
    N --> S
    O --> S
    P --> S
    Q --> S
    S -->|target/project/documents| T["Workspace + Solution + Projects + Documents"]
    S -->|--file + --line + --column| U["FindTextDocumentAsync<br/>PositionResolver.GetPositionAsync"]
    S -->|--symbol| V["ResolveCommandSymbolAsync<br/>RoslynSymbolResolver"]

    U --> W["SemanticModel / SyntaxTree / SourceText"]
    V --> W
    T --> W

    W --> X["Roslyn APIs<br/>SymbolFinder, QuickInfoService,<br/>compilation diagnostics, document symbols"]
    X --> Y["Result models<br/>Output/*.cs"]
    Y --> Z["MarkdownProjection.Render(data)<br/>command-specific markdown renderer"]
    Z --> PR["CliProcessResult<br/>exit code + exact stdout + stderr"]

    H --> PR
    I --> PR
    IA --> Y
    DC --> PR
    PR --> OUT["CliApplication.RunAsync<br/>writes stdout + stderr; returns exit code"]

    C1 --> ERR{"Exception handling"}
    ERR -->|CliUsageException| E2["CliProcessResult stdout error:<br/>error: usage<br/>message<br/>hint<br/>exit 2"]
    ERR -->|OperationCanceledException| E3["CliProcessResult stdout error:<br/>error: canceled<br/>exit 130"]
    ERR -->|Exception| E4["CliProcessResult stdout error:<br/>exception type<br/>message<br/>exit 1"]
    E2 --> PR
    E3 --> PR
    E4 --> PR
```

## Domains

- CLI routing: [Program.cs](../../src/RoslynKit/Program.cs), [CliApplication.cs](../../src/RoslynKit/CliApplication.cs), [CliProcessResult.cs](../../src/RoslynKit/CliProcessResult.cs), [DaemonFallbackWorkspaceCommandRouter.cs](../../src/RoslynKit/DaemonFallbackWorkspaceCommandRouter.cs), [WorkspaceCommandRouter.cs](../../src/RoslynKit/WorkspaceCommandRouter.cs), [DaemonCommandExecutor.cs](../../src/RoslynKit/DaemonCommandExecutor.cs), [CliParser.cs](../../src/RoslynKit/CliParser.cs), [BuiltinCommandRegistry.cs](../../src/RoslynKit/BuiltinCommandRegistry.cs), [RoslynCommandExecutor.cs](../../src/RoslynKit/RoslynCommandExecutor.cs), [InitCommandExecutor.cs](../../src/RoslynKit/InitCommandExecutor.cs); start symbols `Program.Main`, `Program.CreateCliApplication`, `CliApplication.RunAsync`, `CliApplication.ExecuteAsync`, `DaemonFallbackWorkspaceCommandRouter.ExecuteAsync`, `WorkspaceCommandRouter.ExecuteAsync`, `DaemonCommandExecutor.ExecuteAsync`, `CliParser.Parse`, `RoslynCommandExecutor.ExecuteAsync`, `InitCommandExecutor.Execute`; tests [ProgramTests.cs](../../tests/RoslynKit.Tests/ProgramTests.cs), [CliParserTests.cs](../../tests/RoslynKit.Tests/CliParserTests.cs), [CliOutputTests.cs](../../tests/RoslynKit.Tests/CliOutputTests.cs), [DaemonFallbackWorkspaceCommandRouterTests.cs](../../tests/RoslynKit.Tests/DaemonFallbackWorkspaceCommandRouterTests.cs), [InitCommandExecutorTests.cs](../../tests/RoslynKit.Tests/InitCommandExecutorTests.cs), [CommandExecution/](../../tests/RoslynKit.Tests/CommandExecution/), [MarkdownFormatTests.cs](../../tests/RoslynKit.Tests/MarkdownFormatTests.cs).
- Workspace/navigation: `RoslynCommandExecutor.cs`, `RoslynWorkspaceLoader.cs`, `PositionResolver.cs`, `RoslynSymbolResolver.cs`, `RoslynDocumentFilters.cs`, `RoslynSymbolSearch.cs`, `RoslynSignatureHelpService.cs`; start symbols `RoslynWorkspaceLoader.LoadAsync`, `PositionResolver.GetPositionAsync`, `RoslynSymbolResolver.ResolveAsync`, `RoslynSymbolSearch.EnumerateSourceSymbols`; tests `CommandExecution/`, `SymbolsCommandTests.cs`, `CliOutputTests.cs`.
- Intent-based symbol search: [SearchCommandService.cs](../../src/RoslynKit/SearchCommandService.cs), [SearchQueryTokenizer.cs](../../src/RoslynKit/SearchQueryTokenizer.cs), [SearchIndexPathPolicy.cs](../../src/RoslynKit/SearchIndexPathPolicy.cs), [SearchIndexFingerprint.cs](../../src/RoslynKit/SearchIndexFingerprint.cs), [RoslynSearchCorpusBuilder.cs](../../src/RoslynKit/RoslynSearchCorpusBuilder.cs), [SqliteSearchIndex.cs](../../src/RoslynKit/SqliteSearchIndex.cs), [SqliteSearchIndexModels.cs](../../src/RoslynKit/SqliteSearchIndexModels.cs), [SqliteSearchIndexTypeMaps.cs](../../src/RoslynKit/SqliteSearchIndexTypeMaps.cs), [RoslynCommandExecutor.cs](../../src/RoslynKit/RoslynCommandExecutor.cs), and [MarkdownProjection.cs](../../src/RoslynKit/MarkdownProjection.cs); start symbols `SearchCommandService.IndexAsync`, `SearchCommandService.SearchAsync`, `SearchQueryTokenizer.Tokenize*`, `SearchIndexPathPolicy.ResolveAsync`, `SearchIndexFingerprintService.CaptureAsync`, `RoslynSearchCorpusBuilder.BuildAsync`, `SqliteSearchIndex.ReplaceTargetAsync`, and `SqliteSearchIndex.SearchAsync`; tests [SearchCommandTests.cs](../../tests/RoslynKit.Tests/SearchCommandTests.cs), [SearchQueryTokenizerTests.cs](../../tests/RoslynKit.Tests/SearchQueryTokenizerTests.cs), [SearchIndexPolicyTests.cs](../../tests/RoslynKit.Tests/SearchIndexPolicyTests.cs), [RoslynSearchCorpusBuilderTests.cs](../../tests/RoslynKit.Tests/RoslynSearchCorpusBuilderTests.cs), [SearchMarkdownFormatTests.cs](../../tests/RoslynKit.Tests/SearchMarkdownFormatTests.cs), [SqliteSearchIndexTests.cs](../../tests/RoslynKit.Tests/SqliteSearchIndexTests.cs), [SqliteSearchIndexTypeMapsTests.cs](../../tests/RoslynKit.Tests/SqliteSearchIndexTypeMapsTests.cs), parser/command-execution coverage, and packaged-tool SQLite coverage. Search input is English-oriented; supported symbols match navigable non-generated source declarations with existing physical paths inside the target's Git worktree. Dapper hydrates SQLite read rows through strict explicit column maps whose lookup is case-insensitive; Microsoft.Data.Sqlite continues to own provider connections, writer transactions, and schema operations. Source-generated documents, generated paths below `bin` or `obj`, and sources with standard generated-code markers injected from extracted NuGet packages outside the worktree are skipped. Multi-targeted projects, missing project or non-generated source paths, external projects, and external linked non-generated source files are rejected.
- Daemon identity foundation: [GitWorkspaceIdentityResolver.cs](../../src/RoslynKit/GitWorkspaceIdentityResolver.cs), [GitWorkspaceIdentity.cs](../../src/RoslynKit/GitWorkspaceIdentity.cs), [PathCanonicalizer.cs](../../src/RoslynKit/PathCanonicalizer.cs), [RoslynKitBuildInfo.cs](../../src/RoslynKit/RoslynKitBuildInfo.cs), [ProcessCommandRunner.cs](../../src/RoslynKit/ProcessCommandRunner.cs); start symbols `GitWorkspaceIdentityResolver.ResolveAsync`, `PathCanonicalizer.ResolveExistingPath`; tests [GitWorkspaceIdentityResolverTests.cs](../../tests/RoslynKit.Tests/GitWorkspaceIdentityResolverTests.cs). Public client and hidden server routing both consume this seam.
- Daemon endpoint identity: [DaemonIdentity.cs](../../src/RoslynKit/DaemonIdentity.cs), [DaemonEndpointName.cs](../../src/RoslynKit/DaemonEndpointName.cs), [GitWorkspaceIdentity.cs](../../src/RoslynKit/GitWorkspaceIdentity.cs); start symbols `DaemonIdentityResolver.Resolve`, `DaemonEndpointName.Create`; tests [DaemonEndpointNameTests.cs](../../tests/RoslynKit.Tests/DaemonEndpointNameTests.cs) and [GitWorkspaceIdentityResolverTests.cs](../../tests/RoslynKit.Tests/GitWorkspaceIdentityResolverTests.cs). Mutable Git `HEAD` and worktree fingerprints remain outside endpoint selection.
- Daemon framed transport: [DaemonProtocol.cs](../../src/RoslynKit/DaemonProtocol.cs), [DaemonProtocolMessages.cs](../../src/RoslynKit/DaemonProtocolMessages.cs), [DaemonNamedPipe.cs](../../src/RoslynKit/DaemonNamedPipe.cs), [PathCanonicalizer.cs](../../src/RoslynKit/PathCanonicalizer.cs); start symbols `DaemonProtocol.ReadRequestAsync`, `DaemonProtocol.WriteResponseAsync`, `DaemonCommandRequest.Create`, `DaemonNamedPipe.CreateServer`; tests [DaemonProtocolFramingTests.cs](../../tests/RoslynKit.Tests/DaemonProtocolFramingTests.cs), [DaemonProtocolMessageTests.cs](../../tests/RoslynKit.Tests/DaemonProtocolMessageTests.cs), and [NamedPipeDaemonTransportTests.cs](../../tests/RoslynKit.Tests/NamedPipeDaemonTransportTests.cs). This seam owns framing, read-only command validation, and local pipe construction, not daemon hosting, dispatch, process lifecycle, or CLI fallback.
- Git worktree fingerprinting: [GitWorktreeFingerprintService.cs](../../src/RoslynKit/GitWorktreeFingerprintService.cs), [GitWorktreeFingerprint.cs](../../src/RoslynKit/GitWorktreeFingerprint.cs), [GitPorcelainParser.cs](../../src/RoslynKit/GitPorcelainParser.cs), [ProcessCommandRunner.cs](../../src/RoslynKit/ProcessCommandRunner.cs); start symbol `GitWorktreeFingerprintService.CaptureAsync`; tests [GitWorktreeFingerprintServiceTests.cs](../../tests/RoslynKit.Tests/GitWorktreeFingerprintServiceTests.cs). This seam is request-time mutable state and remains separate from daemon compatibility identity.
- Workspace daemon session: [WorkspaceDaemonSession.cs](../../src/RoslynKit/WorkspaceDaemonSession.cs), [GitWorktreeFingerprintService.cs](../../src/RoslynKit/GitWorktreeFingerprintService.cs), [RoslynWorkspaceLoader.cs](../../src/RoslynKit/RoslynWorkspaceLoader.cs), [RoslynCommandExecutor.cs](../../src/RoslynKit/RoslynCommandExecutor.cs); start symbols `WorkspaceDaemonSession.ExecuteAsync`, `WorkspaceDaemonGeneration.LoadAsync`; tests [WorkspaceDaemonSessionTests.cs](../../tests/RoslynKit.Tests/WorkspaceDaemonSessionTests.cs). This seam owns generation lifetime, fingerprint baselines, reload coordination, and bounded clean-reader admission, not daemon hosting, process lifecycle, IPC dispatch, or CLI fallback.
- Workspace daemon lifecycle: [WorkspaceDaemonHost.cs](../../src/RoslynKit/WorkspaceDaemonHost.cs), [WorkspaceDaemonServer.cs](../../src/RoslynKit/WorkspaceDaemonServer.cs), [DaemonServerRunner.cs](../../src/RoslynKit/DaemonServerRunner.cs), [DaemonLifetimeLease.cs](../../src/RoslynKit/DaemonLifetimeLease.cs), [WorkspaceDaemonSession.cs](../../src/RoslynKit/WorkspaceDaemonSession.cs), [DaemonProtocolMessages.cs](../../src/RoslynKit/DaemonProtocolMessages.cs); start symbols `WorkspaceDaemonHost.ExecuteAsync`, `WorkspaceDaemonServer.RunAsync`, `DaemonServerRunner.RunAsync`; tests [WorkspaceDaemonHostTests.cs](../../tests/RoslynKit.Tests/WorkspaceDaemonHostTests.cs), [WorkspaceDaemonServerTests.cs](../../tests/RoslynKit.Tests/WorkspaceDaemonServerTests.cs), [DaemonLifetimeLeaseTests.cs](../../tests/RoslynKit.Tests/DaemonLifetimeLeaseTests.cs), and [ProgramTests.cs](../../tests/RoslynKit.Tests/ProgramTests.cs). This seam owns request registration, deadline and disconnect cancellation, idle timing, status snapshots, graceful stop, hidden-process construction, one-live-server enforcement, and named-pipe dispatch.
- Daemon client and fallback: [DaemonFallbackWorkspaceCommandRouter.cs](../../src/RoslynKit/DaemonFallbackWorkspaceCommandRouter.cs), [DaemonClient.cs](../../src/RoslynKit/DaemonClient.cs), [DaemonPipeClient.cs](../../src/RoslynKit/DaemonPipeClient.cs), [DaemonBootstrapLease.cs](../../src/RoslynKit/DaemonBootstrapLease.cs), [DaemonProcessStarter.cs](../../src/RoslynKit/DaemonProcessStarter.cs), [DaemonCommandExecutor.cs](../../src/RoslynKit/DaemonCommandExecutor.cs); start symbols `DaemonFallbackWorkspaceCommandRouter.ExecuteAsync`, `DaemonClient.ExecuteAsync`, `DaemonPipeClient.SendAsync`, `DaemonBootstrapLease.TryAcquire`, `DaemonProcessStarter.Start`, `DaemonCommandExecutor.ExecuteAsync`; tests [DaemonFallbackWorkspaceCommandRouterTests.cs](../../tests/RoslynKit.Tests/DaemonFallbackWorkspaceCommandRouterTests.cs), [DaemonClientTests.cs](../../tests/RoslynKit.Tests/DaemonClientTests.cs), [DaemonPipeClientTests.cs](../../tests/RoslynKit.Tests/DaemonPipeClientTests.cs), [DaemonBootstrapLeaseTests.cs](../../tests/RoslynKit.Tests/DaemonBootstrapLeaseTests.cs), and [DaemonProcessStarterTests.cs](../../tests/RoslynKit.Tests/DaemonProcessStarterTests.cs). `DaemonClient` owns public connection, handshake validation, startup serialization, same-build self-launch, readiness polling, and typed infrastructure normalization. `DaemonFallbackWorkspaceCommandRouter` owns strict fallback classification, the buffered-stderr warning, and one standalone retry; non-starting lifecycle controls bypass fallback.
- Markdown output contract: `MarkdownProjection.cs`, result model types under `Output/`, [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md); tests `MarkdownFormatTests.cs`, `CliOutputTests.cs`.
- Tooling/packaging: `RoslynKit.csproj`, `InitCommandExecutor.cs`, `scripts/prepare-roslynkit-package.ps1`, `scripts/install-roslynkit-dev.ps1`, `scripts/RoslynKit.Packaging.ps1`, [docs/dev-install.md](../../docs/dev-install.md), [docs/dotnet-tool-release.md](../../docs/dotnet-tool-release.md), [docs/agents/skill-maintenance.md](../../docs/agents/skill-maintenance.md); tests usually start with `CliOutputTests.cs`, `InitCommandExecutorTests.cs`, plus build/pack smoke commands.
- Codex benchmarking: [scripts/benchmark-codex.ps1](../../scripts/benchmark-codex.ps1), [benchmarks/codex-cases.json](../../benchmarks/codex-cases.json), [docs/benchmark-codex.md](../../docs/benchmark-codex.md), and [.agents/skills/benchmark/SKILL.md](../../.agents/skills/benchmark/SKILL.md). The opt-in controller keeps prompts and review criteria outside separate one-commit raw and RoslynKit snapshots, restores both snapshots before timing, gives only the RoslynKit snapshot a prepared `artifacts/roslynkit.db` index, runs children with the snapshot as `--cd`, resolves global `rg` and `roslynkit` from the fully inherited workstation environment, checks both tools in one unmeasured full-access preflight, stops workspace daemons between sessions, uses the active workstation Codex home and configuration while retaining benchmark command-line overrides, and reads token accounting only from current-run JSON Lines events.
- Agent/navigation policy: [AGENTS.md](../../AGENTS.md), [docs/agents/README.md](../../docs/agents/README.md), [.agents/skills/roslynkit/SKILL.md](../../.agents/skills/roslynkit/SKILL.md), [.agents/skills/roslynkit/references/commands.md](../../.agents/skills/roslynkit/references/commands.md), [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md), [.agents/skills/roslynkit-dev/SKILL.md](../../.agents/skills/roslynkit-dev/SKILL.md), [.agents/skills/benchmark/SKILL.md](../../.agents/skills/benchmark/SKILL.md), [.agents/skills/grill-me/SKILL.md](../../.agents/skills/grill-me/SKILL.md), this map.

## Test Routing

- Parser and option validation -> `tests/RoslynKit.Tests/CliParserTests.cs`
- Init scaffolding and guardrails -> `tests/RoslynKit.Tests/InitCommandExecutorTests.cs`
- Command execution and Roslyn navigation flows -> `tests/RoslynKit.Tests/CommandExecution/`
- Help/version/error output -> `tests/RoslynKit.Tests/CliOutputTests.cs`
- Markdown rendering contract -> `tests/RoslynKit.Tests/MarkdownFormatTests.cs`
- Symbol search and document-symbol behavior -> `tests/RoslynKit.Tests/SymbolsCommandTests.cs`
- External daemon startup, lifecycle, reload, fallback, and packaged-tool behavior -> [DaemonProcessIntegrationTests.cs](../../tests/RoslynKit.Tests/DaemonProcessIntegrationTests.cs), [DaemonFallbackProcessIntegrationTests.cs](../../tests/RoslynKit.Tests/DaemonFallbackProcessIntegrationTests.cs), and [PackagedToolProcessIntegrationTests.cs](../../tests/RoslynKit.Tests/PackagedToolProcessIntegrationTests.cs), using [DaemonProcessTestArea.cs](../../tests/RoslynKit.Tests/DaemonProcessTestArea.cs)
- Repo and fixture path helpers -> `tests/RoslynKit.Tests/TestPaths.cs`

## Commands

- Build: `dotnet build .\RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"`
- Test: `dotnet test .\RoslynKit.slnx`
- Run: `dotnet run --project .\src\RoslynKit -- help`
- Pack: `dotnet pack .\src\RoslynKit\RoslynKit.csproj`
- Workspace graph: `dotnet run --project .\tests\RoslynKit.WorkspaceGraphDump -- .\RoslynKit.slnx`

## Navigation Rules

- Use `roslynkit-dev` for repo-local C# semantic inspection unless the task is explicitly about the stable global tool.
- Prefer tests before implementation when available.
- For command or feature tracing, follow the runtime spine first, then use RoslynKit or direct line reads for the narrow unclear hop; use broad literal search only after the spine fails.
- Prefer RoslynKit `symbols`, `document-symbols`, `definition`, `references`, `implementations`, `type-definition`, `quick-info`, `signature-help`, `document-lines`, and `symbol-source` over broad source reads.
- Use sparse XML comments surfaced by documentation-enabled RoslynKit output as next-hop hints, not as exhaustive documentation.
- Do not use Atlas as a file inventory, test inventory, symbol graph, reference graph, or source cache.
- Ignore first: `artifacts/`, `TestResults/`, `.vs/`, `Visual Studio 18/`, `bin/`, `obj/`, `*.nupkg`.

Last verified: `2026-08-08`
