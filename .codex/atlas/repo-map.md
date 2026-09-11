# Repository Map

Last verified: 2026-09-11

RoslynKit is a .NET 10 tool for deterministic, read-only C# inspection. A client-owned standard-input/output (stdio) Model Context Protocol (MCP) server retains live Roslyn workspaces. Standalone commands use the same engine with short-lived ownership. Repository-local SQLite persists search indexes, not semantic catalogs or completed query answers. [docs/architecture.md](../../docs/architecture.md) owns the accepted rewrite decisions.

## Navigation Strategy

- Start with [AGENTS.md](../../AGENTS.md) for active repository rules.
- Use this map to choose a domain and first read order.
- Read tests before implementation when coverage exists.
- Prefer RoslynKit semantic commands for C# symbols and narrow line-range reads after a path is resolved.
- Use literal search for Markdown, configuration, scripts, and exact text.
- Stop after five source files and state a hypothesis before expanding the read set.

## Architecture Spine

```text
args
  -> Program
       -> serve -> McpServerHost: help / query
            -> McpQuery: explicit repository and path normalization
            -> McpWorkspacePool: bounded server-owned workers
            -> RepositoryWorkerClient / RepositoryWorkerHost: private stdio
                 -> RepositoryWorkspaceSession: snapshots and reader leases
                 -> RepositoryFileWatcher / WorkspaceInputManifest: saved inputs
                 -> LiveSearchIndex: independent background publication
       -> CLI -> CliParser -> CliApplication -> WorkspaceCommandRouter
            -> StandaloneWorkspaceLoader or short-lived search session

workspace materialization
  -> RepositoryContext / RepositoryProjectDiscovery
  -> WorkspacePreparation / DotnetSdkResolver / WorkspaceSupportValidator
  -> RoslynWorkspaceLoader -> captured Solution
       -> RoslynCommandExecutor -> PositionResolver / RoslynSymbolResolver
       -> RoslynSearchCorpusBuilder -> SqliteSearchIndex
  -> MarkdownProjection
```

The default CLI scope is the nearest standard `.git/` directory; MCP queries require an explicit absolute root. RoslynKit discovers every tracked or unignored `.csproj` and loads the resulting repository project forest, including disconnected components. The default search index path is `.roslynkit/roslynkit.db`; `.roslynkit/.gitignore` excludes the database and SQLite sidecars without modifying the root `.gitignore`.

Explicit `.slnx`, `.sln`, `.slnf`, `.csproj`, and repository-directory targets remain supported. Linked worktrees and other `.git` indirection files are intentionally unsupported in the initial repository-discovery contract.

## Runtime Domains

### Entry, Parsing, and Command Contract

**Read first**

1. [src/RoslynKit/Program.cs](../../src/RoslynKit/Program.cs)
2. [src/RoslynKit/CliParser.cs](../../src/RoslynKit/CliParser.cs)
3. [src/RoslynKit/BuiltinCommandRegistry.cs](../../src/RoslynKit/BuiltinCommandRegistry.cs)
4. [src/RoslynKit/CliApplication.cs](../../src/RoslynKit/CliApplication.cs)
5. [tests/RoslynKit.Tests/CliParserTests.cs](../../tests/RoslynKit.Tests/CliParserTests.cs)

Command metadata in `BuiltinCommandRegistry` generates [.agents/skills/roslynkit/references/commands.md](../../.agents/skills/roslynkit/references/commands.md). Public command changes require regeneration with [tools/RoslynKit.CommandDocs.cs](../../tools/RoslynKit.CommandDocs.cs).

### Repository and Workspace Resolution

**Read first**

1. [src/RoslynKit/RepositoryContext.cs](../../src/RoslynKit/RepositoryContext.cs)
2. [src/RoslynKit/RepositoryProjectDiscovery.cs](../../src/RoslynKit/RepositoryProjectDiscovery.cs)
3. [src/RoslynKit/RoslynWorkspaceLoader.cs](../../src/RoslynKit/RoslynWorkspaceLoader.cs)
4. [tests/RoslynKit.Tests/RepositoryDiscoveryTests.cs](../../tests/RoslynKit.Tests/RepositoryDiscoveryTests.cs)
5. [tests/RoslynKit.Tests/CommandExecution/WorkspaceCommandExecutionTests.cs](../../tests/RoslynKit.Tests/CommandExecution/WorkspaceCommandExecutionTests.cs)

`RepositoryContext` establishes the repository and index boundary. `RepositoryProjectDiscovery` uses Git-visible projects for initial discovery, not as the complete compiler input manifest. `WorkspacePreparation` evaluates supported single-target SDK-style projects and restores dependencies when needed. `RoslynWorkspaceLoader` opens the project forest or explicit target and avoids reopening transitively loaded projects. Each retained worker isolates process-global SDK registration.

### Retained Workspace and MCP Lifecycle

**Read first**

1. [src/RoslynKit/McpServerHost.cs](../../src/RoslynKit/McpServerHost.cs)
2. [src/RoslynKit/McpWorkspacePool.cs](../../src/RoslynKit/McpWorkspacePool.cs)
3. [src/RoslynKit/RepositoryWorkerHost.cs](../../src/RoslynKit/RepositoryWorkerHost.cs)
4. [src/RoslynKit/RepositoryWorkspaceSession.cs](../../src/RoslynKit/RepositoryWorkspaceSession.cs)
5. [src/RoslynKit/WorkspaceInputManifest.cs](../../src/RoslynKit/WorkspaceInputManifest.cs)

The public transport exposes only `help` and `query`. The latter includes the logical `refresh` operation. The pool retains bounded idle-evictable repository/scope workers. A session applies existing source text incrementally and replaces workspace owners for structural changes. Queries lease immutable solutions; retired owners live until their last reader releases them. Watcher notifications drive ordinary synchronization; periodic reconciliation and explicit refresh perform broader verification. Ordinary warm queries do not hash the entire input set.

SDK/restore problems route through [src/RoslynKit/WorkspacePreparation.cs](../../src/RoslynKit/WorkspacePreparation.cs); process lifetime problems route through [src/RoslynKit/RepositoryWorkerClient.cs](../../src/RoslynKit/RepositoryWorkerClient.cs). [src/RoslynKit/RepositoryFileWatcher.cs](../../src/RoslynKit/RepositoryFileWatcher.cs) owns filesystem notifications, not polling or compilation.

### Persistent Search

**Read first**

1. [src/RoslynKit/LiveSearchIndex.cs](../../src/RoslynKit/LiveSearchIndex.cs)
2. [src/RoslynKit/SearchCommandService.cs](../../src/RoslynKit/SearchCommandService.cs)
3. [src/RoslynKit/SqliteSearchIndex.cs](../../src/RoslynKit/SqliteSearchIndex.cs)
4. [src/RoslynKit/RoslynSearchCorpusBuilder.cs](../../src/RoslynKit/RoslynSearchCorpusBuilder.cs)
5. [tests/RoslynKit.Tests/SearchCommandTests.cs](../../tests/RoslynKit.Tests/SearchCommandTests.cs)

SQLite owns Full-Text Search 5 (FTS5) and Best Matching 25 (BM25) declaration retrieval, navigation IDs/locations/excerpts, and compatible scope/input metadata. It does not own exact semantic answers, relationships, or an operation-result cache. Schema migration removes obsolete catalog tables. A database writer lease spans corpus construction and validated atomic publication. Search waits for known-outdated data; semantic queries do not wait for index work. Text-only mode uses a separate partition without MSBuild.

### Live Semantic Navigation

**Read first**

1. [src/RoslynKit/RoslynCommandExecutor.cs](../../src/RoslynKit/RoslynCommandExecutor.cs)
2. [src/RoslynKit/StandaloneWorkspaceLoader.cs](../../src/RoslynKit/StandaloneWorkspaceLoader.cs)
3. [src/RoslynKit/PositionResolver.cs](../../src/RoslynKit/PositionResolver.cs)
4. [src/RoslynKit/RoslynSymbolResolver.cs](../../src/RoslynKit/RoslynSymbolResolver.cs)
5. [tests/RoslynKit.Tests/CommandExecution/SemanticCommandExecutionTests.cs](../../tests/RoslynKit.Tests/CommandExecution/SemanticCommandExecutionTests.cs)

All semantic commands execute against live Roslyn. The caller-owned executor overload uses a captured loader view without taking ownership; the standalone overload prepares and owns a cold workspace. Neither persists completed results. Snapshot construction must not use Roslyn workspace apply operations that write source files.

### Rendering and Output Contract

**Read first**

1. [src/RoslynKit/MarkdownProjection.cs](../../src/RoslynKit/MarkdownProjection.cs)
2. [src/RoslynKit/Output/](../../src/RoslynKit/Output/)
3. [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md)
4. [tests/RoslynKit.Tests/CliOutputTests.cs](../../tests/RoslynKit.Tests/CliOutputTests.cs)

Implicit repository index and search output uses `scope: repository` plus `repository:`. Explicit scopes retain `target:`. Symbol chaining uses emitted documentation-comment `id:` values and `loc:` coordinates; no RoslynKit-specific opaque reference identifier exists.

## Packaging and Skill Maintenance

- [src/RoslynKit/RoslynKit.csproj](../../src/RoslynKit/RoslynKit.csproj) defines the .NET tool package.
- PowerShell entrypoints use lowercase, action-first filenames under [scripts/](../../scripts/). Dot-sourced helpers live under [scripts/common/](../../scripts/common/) with noun filenames.
- [scripts/pack.ps1](../../scripts/pack.ps1) prepares release artifacts.
- [scripts/test-commands.ps1](../../scripts/test-commands.ps1) invokes every runtime command against deterministic fixtures, guards command coverage against runtime help, and aggregates automated failures. Manual Bash commands live only in the release guide.
- [scripts/common/packaging.ps1](../../scripts/common/packaging.ps1) shares isolated package installation, scoped environment restoration, and unchanged-package verification between package testing and global replacement.
- [scripts/common/process.ps1](../../scripts/common/process.ps1) shares native process execution with separate output streams, argument preservation, and optional timeouts across packaging and command tests. Packaging anchors .NET commands to the checkout root; relative path overrides follow the current PowerShell location.
- [scripts/test-package.ps1](../../scripts/test-package.ps1) exhaustively tests the exact local package through that isolated installation.
- [scripts/install-global.ps1](../../scripts/install-global.ps1) explicitly replaces the global tool with the exact staged local package, while [scripts/test-global.ps1](../../scripts/test-global.ps1) runs the same exhaustive suite through the global command path.
- [docs/dotnet-tool-release.md](../../docs/dotnet-tool-release.md) owns release preparation, WSL global replacement, grouped manual Bash command checks, and the separate manual NuGet.org upload. There is no release workflow skill; existing scripts own package creation and installation, while the guide owns the operator's sequence and acceptance criteria.
- [.agents/skills/roslynkit/](../../.agents/skills/roslynkit/) is the canonical embedded stable skill bundle.
- [src/RoslynKit/InitCommandExecutor.cs](../../src/RoslynKit/InitCommandExecutor.cs) scaffolds that bundle for supported coding agents.
- [docs/agents/skill-maintenance.md](../../docs/agents/skill-maintenance.md) defines synchronization rules.

## Validation Routes

| Change | Focused validation |
|---|---|
| Parser or command metadata | `CliParserTests`, `CommandReferenceMarkdownTests`, generated command-reference check |
| Repository discovery | `RepositoryDiscoveryTests`, `WorkspaceCommandExecutionTests` |
| Search schema or freshness | `SqliteSearchIndexTests`, `SearchCommandTests`, `SearchCliContractTests`, migration tests |
| Retained lifecycle and SDKs | Session, input-manifest, MCP protocol/pool/process, SDK-resolution, and workspace-preparation tests |
| Semantic navigation | `SemanticCommandExecutionTests`, `SymbolContextCommandExecutionTests` |
| Rendering | `CliOutputTests`, [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md) |
| Packaging and PowerShell helpers | [tests/PowerShell/test-portability.ps1](../../tests/PowerShell/test-portability.ps1) on Windows and Linux, `PackagedToolProcessIntegrationTests`, [docs/dotnet-tool-release.md](../../docs/dotnet-tool-release.md) |

Run post-change formatting and the smallest targeted test set first. Run the full solution build and test suite before publishing changes.
