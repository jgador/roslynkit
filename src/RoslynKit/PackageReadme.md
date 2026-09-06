# roslynkit

`roslynkit` is a .NET tool for deterministic, read-only Roslyn-powered C# analysis. A client-owned Model Context Protocol (MCP) server retains workspaces across requests; the standalone command-line interface (CLI) uses the same engine.

The initial retained-workspace implementation is in place. The repository architecture document records the accepted design and current tradeoffs. Windows execution and large-repository performance validation remain release work.

## Install from NuGet.org

RoslynKit targets .NET 10. Install the .NET 10 Software Development Kit (SDK) first if `dotnet --version` reports an older SDK or `dotnet` is not available.

```powershell
dotnet --version
```

Install the global tool:

```powershell
dotnet tool install --global roslynkit
roslynkit version
```

`roslynkit version` confirms that the installed tool is callable from the current shell. If the install succeeds but `roslynkit` is not found, open a new shell or add the .NET global tools directory to `PATH`:

- Windows: `%USERPROFILE%/.dotnet/tools`
- macOS/Linux: `$HOME/.dotnet/tools`

To update an existing install:

```powershell
dotnet tool update --global roslynkit
roslynkit version
```

## Install from a local folder feed

```powershell
dotnet tool install --global roslynkit --add-source <local-feed-path> --version <version> --ignore-failed-sources
roslynkit version
```

To update an existing local install:

```powershell
dotnet tool update --global roslynkit --add-source <local-feed-path> --version <version> --ignore-failed-sources
roslynkit version
```

## Set up a repository

For optional CLI skill guidance, run `init` once from an ordinary Git repository root. The current directory must contain a `.git` directory; linked worktrees and other `.git` indirection files are unsupported.

```powershell
cd ./path/to/MyApp
Test-Path .git -PathType Container
roslynkit init
```

If `Test-Path .git -PathType Container` prints `False`, select an ordinary Git repository root before running `roslynkit init`.

The default target is Codex at `.agents/skills/roslynkit/`. Use `--agent claude`, `--agent copilot`, or `--agent all` for other supported agent folders:

```powershell
roslynkit init --agent claude
roslynkit init --agent copilot
roslynkit init --agent all
```

Agent targets map to these folders:

- `codex` -> `.agents/skills/roslynkit/`
- `claude` -> `.claude/skills/roslynkit/`
- `copilot` -> `.github/skills/roslynkit/`

Existing generated files are preserved when content is identical and rejected when content differs. Add `--overwrite` only when the scaffolded RoslynKit skill files should be replaced.

## Start the MCP server

Configure an MCP client to launch RoslynKit over standard input and standard output (stdio):

```text
roslynkit serve
```

The server exposes exactly `help` and `query`. `help` accepts an optional `command`; `query` requires an absolute `repositoryRoot` and an `args` array:

```json
{
  "repositoryRoot": "/absolute/path/to/MyApp",
  "args": ["symbols", "--query", "MyService", "--exact"]
}
```

Use a root in the server's filesystem namespace. Every query identifies its resolved repository root, and relative path options resolve from that root. Add `--target` inside `args` when a solution, solution filter, project, or repository directory should limit the scope.

`serve --max-workspaces <count>` changes the default limit of four retained scopes. The client owns the server and its private workers; disconnecting shuts them down. Least-recently-used idle scopes can be evicted and reloaded. The limit counts scopes rather than bytes of memory.

Saved C# text edits update retained Roslyn snapshots. Structural changes replace the workspace; readers already using an older snapshot can finish. File watchers and background reconciliation detect changes, with some detection delay. After an agent-initiated build attempt completes, including a failed build, await `query` with `args: ["refresh"]` and the same optional `--target` before further analysis. `refresh` synchronizes saved inputs; it is not a third tool, a standalone command, a full build, or a build hook.

Each repository selects an installed SDK using normal rules, including `global.json`; private worker processes isolate different selections. RoslynKit does not install SDKs. Dependency restore runs when needed and can be disabled with `serve --no-restore` or `--no-restore` on a workspace operation. Ordinary source edits do not trigger restore. MSBuild evaluation and restore run with the process's permissions; read-only analysis is not a sandbox or repository trust gate.

Supported repositories use ordinary `.git` directories and modern .NET C# projects with one target framework per project. Solution files are optional. Legacy .NET Framework, multi-targeted projects, non-Git folders, and `.git` indirection layouts are unsupported. Platform-specific workloads depend on installed host capabilities.

## First commands

Confirm RoslynKit can discover and load the repository project forest:

```powershell
roslynkit workspace
roslynkit diagnostics
```

The standalone CLI finds the nearest standard `.git/` directory and loads every tracked or unignored `.csproj`. Use optional `--target` to narrow a command to a `.slnx`, `.sln`, `.slnf`, `.csproj`, or repository-directory scope. Each invocation owns a short-lived workspace and can incur cold startup; it does not attach to a running MCP server. Workspace operations accept `--no-restore`.

## Search by code intent

`search` finds C# declarations from an English-oriented question. It uses SQLite Full-Text Search 5 (FTS5) with internal Best Matching 25 (BM25) ranking. `index` prepares the same persistent index explicitly.

The repository and database are implicit:

```powershell
roslynkit index
roslynkit search --query "where is request validation"
```

The search index lives at `.roslynkit/roslynkit.db`; RoslynKit creates `.roslynkit/.gitignore` for the database and its SQLite write-ahead logging (WAL) sidecars. The database stores separate repository and explicit-target partitions containing retrieval fields, navigation identities, locations, excerpts, and input metadata. `search` prepares the index automatically and waits when relevant inputs are known to have changed; `index --rebuild` recreates the selected partition. Independent local clients coordinate compatible index writes while retaining separate workspaces. Network-hosted shared databases are outside the supported scope.

Exact symbols, definitions, references, implementations, and compiler context come from live Roslyn snapshots. There is no separate persistent semantic catalog or application-level cache of completed answers. Background search indexing does not block semantic queries after workspace synchronization.

The index requires every indexed project and non-generated source document to have an existing physical path inside the repository. It rejects missing or external project and source paths. Generated source documents are skipped, including source-generated documents, generated paths below `bin` or `obj`, and sources with standard generated-code markers injected from extracted NuGet packages outside the worktree.

Search output is for agent-mediated follow-up. Inspect several ranked results, then pass a returned `id:` or `loc:` to an existing navigation command. RoslynKit does not read search results from standard input.

## Main workflows

- Retain workspaces through the client-owned `serve` process and its `help` and `query` tools.
- Print tool metadata with `version` or top-level `--version`.
- Enumerate workspace documents with `workspace`, including generated, additional, or analyzer-config documents when requested.
- Prepare and search a repository-local C# full-text index with `index` and `search`.
- Search, navigate, and inspect C# symbols with commands such as `symbols`, `definition`, `references`, `quick-info`, and `symbol-source`.
- Read resolved documents with `document-text`, `document-lines`, or `document-symbols`.
- Scaffold the RoslynKit skill bundle into a Git repository with `init`.

Document commands use `--file <path>` as the document selector. Relative paths resolve from the CLI's current working directory or the MCP query's explicit repository root. Ambiguous document contexts can be narrowed with `--project`, `--tfm`, or `--document-kind`; these selectors do not enable multi-targeted projects.

CLI results are compact Markdown with non-zero exit codes for failures. MCP wraps the same text in tool results, identifies the repository root, and sets `isError: true` on failures. During `serve`, stdout contains only protocol messages; diagnostics go to stderr. See [.agents/skills/roslynkit/references/output.md](https://github.com/jgador/roslynkit/blob/master/.agents/skills/roslynkit/references/output.md) for the shared output contract.

See [README.md](https://github.com/jgador/roslynkit#readme) for usage guidance and [.agents/skills/roslynkit/references/commands.md](https://github.com/jgador/roslynkit/blob/master/.agents/skills/roslynkit/references/commands.md) for the generated runtime command reference. Side-by-side prerelease dev installs live in [docs/dev-install.md](https://github.com/jgador/roslynkit/blob/master/docs/dev-install.md), and maintainer packaging steps live in [docs/dotnet-tool-release.md](https://github.com/jgador/roslynkit/blob/master/docs/dotnet-tool-release.md) in the same repository:

[RoslynKit on GitHub](https://github.com/jgador/roslynkit)
