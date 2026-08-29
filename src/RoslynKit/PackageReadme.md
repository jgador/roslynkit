# roslynkit

`roslynkit` is a .NET tool for deterministic, read-only Roslyn-powered C# code intelligence in terminal and coding-agent workflows.

## Install from NuGet.org

RoslynKit targets .NET 10. Install the .NET 10 SDK first if `dotnet --version` reports an older SDK or `dotnet` is not available.

```powershell
dotnet --version
```

Install the global tool:

```powershell
dotnet tool install --global roslynkit
roslynkit version
```

`roslynkit version` confirms that the installed tool is callable from the current shell. If the install succeeds but `roslynkit` is not found, open a new shell or add the .NET global tools directory to `PATH`:

- Windows: `%USERPROFILE%\.dotnet\tools`
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

After installing the tool, run `init` once in each Git repository where coding agents should use RoslynKit. Run it from the repository root, because `init` checks the current directory for a `.git` directory or file.

```powershell
cd C:\repo\MyApp
Test-Path .git
roslynkit init
```

If `Test-Path .git` prints `False`, change to the repository root before running `roslynkit init`. The command fails from a parent folder or nested source folder that does not contain `.git`.

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

## First commands

Confirm RoslynKit can discover and load the repository project forest:

```powershell
roslynkit workspace
roslynkit diagnostics
```

RoslynKit finds the nearest standard `.git/` directory and loads every tracked or unignored `.csproj`. Use optional `--target` to narrow a command to a `.slnx`, `.sln`, `.slnf`, `.csproj`, or repository-directory scope.

## Search by code intent

`search` finds C# declarations from an English-oriented question. It uses SQLite Full-Text Search 5 (FTS5) with internal Best Matching 25 (BM25) ranking. `index` prepares the same persistent index explicitly.

The repository and database are implicit:

```powershell
roslynkit index
roslynkit search --query "where is request validation"
```

The catalog lives at `.roslynkit/roslynkit.db`; RoslynKit creates `.roslynkit/.gitignore` for the database and its SQLite write-ahead logging (WAL) sidecars. The database stores separate repository and explicit-target partitions, project paths, declaration source paths, exact symbol metadata, comments, project references, and key semantic relationships. `search` refreshes stale records automatically; `index --rebuild` forces a selected partition rebuild. The index supports projects with one target framework and requires every indexed project and non-generated source document to have an existing physical path inside the repository. It rejects missing or external project and source paths. Generated source documents are skipped, including source-generated documents, generated paths below `bin` or `obj`, and sources with standard generated-code markers injected from extracted NuGet packages outside the worktree.

Fresh catalog data can answer exact symbol listing, symbol definitions, declaration source, and implementations without loading an MSBuild workspace. The first exact references query uses Roslyn and stores its bounded result for identical later requests. Other compiler-context operations continue to load Roslyn in the short-lived command process.

Search output is for agent-mediated follow-up. Inspect several ranked results, then pass a returned `id:` or `loc:` to an existing navigation command. RoslynKit does not read search results from standard input.

## Main workflows

- Print tool metadata with `version` or top-level `--version`.
- Enumerate workspace documents with `workspace`, including generated, additional, or analyzer-config documents when requested.
- Prepare and search a repository-local C# full-text index with `index` and `search`.
- Search, navigate, and inspect C# symbols with commands such as `symbols`, `definition`, `references`, `quick-info`, and `symbol-source`.
- Read resolved documents with `document-text`, `document-lines`, or `document-symbols`.
- Scaffold the RoslynKit skill bundle into a Git repository with `init`.

Document commands use `--file <path>` as the document selector. Relative paths resolve from the current working directory, and ambiguous linked or multi-targeted files can be narrowed with `--project`, `--tfm`, or `--document-kind`.

See [README.md](https://github.com/jgador/roslynkit#readme) for usage guidance and [.agents/skills/roslynkit/references/commands.md](https://github.com/jgador/roslynkit/blob/master/.agents/skills/roslynkit/references/commands.md) for the generated runtime command reference. Side-by-side prerelease dev installs live in [docs/dev-install.md](https://github.com/jgador/roslynkit/blob/master/docs/dev-install.md), and maintainer packaging steps live in [docs/dotnet-tool-release.md](https://github.com/jgador/roslynkit/blob/master/docs/dotnet-tool-release.md) in the same repository:

[RoslynKit on GitHub](https://github.com/jgador/roslynkit)
