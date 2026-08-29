# Repository Map

Last verified: 2026-04-03

RoslynKit is a .NET 10 command-line tool for deterministic, read-only C# inspection. Each invocation is a short-lived process. Reusable state lives in a repository-local SQLite semantic catalog rather than a daemon, named pipe, socket, or Model Context Protocol (MCP) server.

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
  -> CliParser
  -> CliApplication
  -> RoslynCommandExecutor
       -> index/search
            -> RepositoryContext
            -> RepositoryProjectDiscovery
            -> SearchCommandService
            -> RoslynWorkspaceLoader or TextOnlySearchCorpusBuilder
            -> RoslynSearchCorpusBuilder
            -> SqliteSearchIndex
       -> semantic navigation
            -> RepositoryContext
            -> CatalogCommandService
                 -> fresh SQLite answer when supported
                 -> otherwise fall through
            -> RoslynWorkspaceLoader
            -> PositionResolver / SymbolResolver
            -> Roslyn operation
            -> optional lazy operation-cache write
       -> MarkdownProjection
```

The default scope is the nearest standard `.git/` directory. RoslynKit discovers every tracked or unignored `.csproj` and loads the resulting repository project forest, including disconnected components. The default catalog path is `.roslynkit/roslynkit.db`; `.roslynkit/.gitignore` excludes the database and SQLite sidecars without modifying the root `.gitignore`.

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
5. [tests/RoslynKit.Tests/WorkspaceCommandExecutionTests.cs](../../tests/RoslynKit.Tests/WorkspaceCommandExecutionTests.cs)

`RepositoryContext` establishes the repository and default catalog boundary. `RepositoryProjectDiscovery` uses Git-visible files as the project source of truth. `RoslynWorkspaceLoader` opens the implicit project forest or an explicit target and avoids reopening projects already loaded transitively.

### Search and Semantic Catalog

**Read first**

1. [src/RoslynKit/SearchCommandService.cs](../../src/RoslynKit/SearchCommandService.cs)
2. [src/RoslynKit/SqliteSearchIndex.cs](../../src/RoslynKit/SqliteSearchIndex.cs)
3. [src/RoslynKit/SqliteSearchIndex.Catalog.cs](../../src/RoslynKit/SqliteSearchIndex.Catalog.cs)
4. [src/RoslynKit/RoslynSearchCorpusBuilder.cs](../../src/RoslynKit/RoslynSearchCorpusBuilder.cs)
5. [tests/RoslynKit.Tests/SqliteSemanticCatalogTests.cs](../../tests/RoslynKit.Tests/SqliteSemanticCatalogTests.cs)

The catalog owns:

- Full-Text Search 5 (FTS5) and Best Matching 25 (BM25) declaration retrieval.
- Exact symbol identity, project, accessibility, static state, containing symbol, declaration location, and UTF-16 source span.
- XML summaries and structured ordinary comments.
- Project references and containment, inheritance, interface implementation, and override edges.
- Lazy serialized results for bounded live operations such as exact reference queries.

Search validates source fingerprints and republishes search and semantic data atomically. Text-only mode uses a separate repository partition and does not load `MSBuildWorkspace`.

### Cache-First Semantic Navigation

**Read first**

1. [src/RoslynKit/RoslynCommandExecutor.cs](../../src/RoslynKit/RoslynCommandExecutor.cs)
2. [src/RoslynKit/CatalogCommandService.cs](../../src/RoslynKit/CatalogCommandService.cs)
3. [src/RoslynKit/PositionResolver.cs](../../src/RoslynKit/PositionResolver.cs)
4. [src/RoslynKit/SymbolResolver.cs](../../src/RoslynKit/SymbolResolver.cs)
5. [tests/RoslynKit.Tests/SemanticCommandExecutionTests.cs](../../tests/RoslynKit.Tests/SemanticCommandExecutionTests.cs)

A fresh catalog can answer exact `symbols`, symbol-based `definition`, `symbol-source`, and `implementations`. A matching cached `references` invocation can also complete from SQLite. Missing or unsupported catalog answers fall through to a newly loaded Roslyn workspace; live reference results are persisted only when a fresh catalog already exists.

Position-based operations, fuzzy symbol queries, symbol context, quick info, signature help, diagnostics, and generated-document operations remain live Roslyn paths.

### Rendering and Output Contract

**Read first**

1. [src/RoslynKit/MarkdownProjection.cs](../../src/RoslynKit/MarkdownProjection.cs)
2. [src/RoslynKit/Output/](../../src/RoslynKit/Output/)
3. [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md)
4. [tests/RoslynKit.Tests/CliOutputTests.cs](../../tests/RoslynKit.Tests/CliOutputTests.cs)

Implicit repository index and search output uses `scope: repository` plus `repository:`. Explicit scopes retain `target:`. Symbol chaining uses emitted documentation-comment `id:` values and `loc:` coordinates; no RoslynKit-specific opaque reference identifier exists.

## Packaging and Skill Maintenance

- [src/RoslynKit/RoslynKit.csproj](../../src/RoslynKit/RoslynKit.csproj) defines the .NET tool package.
- [scripts/prepare-roslynkit-package.ps1](../../scripts/prepare-roslynkit-package.ps1) prepares release artifacts.
- [docs/dotnet-tool-release.md](../../docs/dotnet-tool-release.md) owns stable release and smoke-test instructions.
- [.agents/skills/roslynkit/](../../.agents/skills/roslynkit/) is the canonical embedded stable skill bundle.
- [src/RoslynKit/SkillScaffoldService.cs](../../src/RoslynKit/SkillScaffoldService.cs) scaffolds that bundle for supported coding agents.
- [docs/agents/skill-maintenance.md](../../docs/agents/skill-maintenance.md) defines synchronization rules.

## Validation Routes

| Change | Focused validation |
|---|---|
| Parser or command metadata | `CliParserTests`, `CommandReferenceMarkdownTests`, generated command-reference check |
| Repository discovery | `RepositoryDiscoveryTests`, `WorkspaceCommandExecutionTests` |
| Search schema or freshness | `SqliteSearchIndexTests`, `SqliteSemanticCatalogTests`, `SearchCliContractTests` |
| Semantic navigation | `SemanticCommandExecutionTests`, `SymbolContextCommandExecutionTests` |
| Rendering | `CliOutputTests`, [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md) |
| Packaging | `PackagedToolProcessIntegrationTests`, [docs/dotnet-tool-release.md](../../docs/dotnet-tool-release.md) |

Run post-change formatting and the smallest targeted test set first. Run the full solution build and test suite before publishing changes.
