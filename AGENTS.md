# Repository Guidelines

## Canonical Docs

Use one source of truth per topic:

- `README.md`: user-facing command list, output format overview, packaging, and quick-start commands.
- `docs/dev-install.md`: semi-manual side-by-side prerelease installation for RoslynKit development.
- `docs/local-repository-reference.md`: local reference repositories for Roslyn APIs, Git CLI style, EF Core tooling conventions, and VS Code C# language-server wiring.
- `docs/skill-maintenance.md`: ownership and synchronization rules for the checked-in RoslynKit skill files.
- `AGENTS.md`: agent-specific working rules, safety rules, and repo workflow expectations.

Keep this file focused on contributor and agent workflow rules. Do not restate full command reference material here.

## Project Structure & Module Organization

RoslynKit is a .NET 10 command-line tool. Production code lives under `src/RoslynKit`, with the console entrypoint in `Program.cs`, CLI parsing in `CliParser.cs`, command metadata in `BuiltinCommandRegistry.cs`, and Roslyn execution logic in `RoslynCommandExecutor.cs`. Tests live under `tests/RoslynKit.Tests`, and repo-local test-side utilities live under `tests/`. Repository documentation lives under `docs/`; use `docs/local-repository-reference.md` before searching remote repositories for Roslyn, Git, EF Core, or VS Code C# implementation references.

## Build, Test, and Development Commands

- `dotnet restore .\RoslynKit.slnx` restores packages for the solution.
- `dotnet build .\RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"` builds the CLI and tests with concise output.
- `dotnet test .\RoslynKit.slnx` runs the xUnit test suite through Microsoft Testing Platform.
- `dotnet run --project .\src\RoslynKit -- help` runs the CLI locally.
- `dotnet pack .\src\RoslynKit\RoslynKit.csproj` creates the .NET tool package.

## Agent Workflow

Verify conclusions against current files, tests, docs, and command output.

Do not use Repository Synapse in this repository. Do not run `synapse ensure`, `synapse recall`, `synapse tests`, or any other command that creates `.synapse/` repo-local cache files. Use Atlas, RoslynKit, scout agents, and direct file/test inspection instead.

### RoslynKit Default Semantic Inspection

When the task is ordinary C# semantic inspection inside this RoslynKit repo, use `.agents\skills\roslynkit-dev\SKILL.md` first. Treat that dev skill as the repo-default route for declarations, symbol structure, definitions, references, implementations, types, signatures, generated documents, and similar Roslyn-backed inspection work.

Use `.agents\skills\roslynkit\SKILL.md` only when the task is explicitly about the stable global tool behavior or the released stable workflow.

For literal search, prose inspection, non-C# files, or RoslynKit workspace-load failures, fall back to the terminal-native tool for the current platform instead of forcing RoslynKit.

### Scout-First Repo Search

Use the `scout` sub-agent for repo discovery when the current agent environment exposes it and any of these are true:

- the user asks to find, trace, investigate, or discover which files matter;
- the user did not name an exact target file;
- more than one top-level area may be relevant;
- the likely read set is more than 3 files.

Skip `scout` when one obvious target file is already known or the task is a single-file explanation or edit.

When using `scout`:

- ground first in `AGENTS.md`, `README.md`, `docs/local-repository-reference.md`, and any directly named files;
- normalize and de-overlap scopes before spawning;
- spawn one `scout` per disjoint scope;
- prefer these default scopes when the area is unclear: `src/RoslynKit/`, `tests/RoslynKit.Tests/`, `docs/`, repo-root config files, and `.agents/` or `.codex/` if agent packaging is involved.

Every scout prompt must include `assigned_scope`, `search_goal`, known keywords or filenames, and the required response format. Every scout must return `assigned_scope`, `files_examined`, `likely_relevant_files`, `evidence`, `handoff_paths`, `short_summary`, and `confidence`. After scouts return, inspect `likely_relevant_files` locally before deeper tracing.

## Repository Atlas Reading Policy

- Use `.codex/atlas/` before broad source reading.
- Use `atlas-router` when the architecture/domain is unclear.
- Use `scout` when files are unclear inside a bounded scope.
- For C# semantic inspection after candidate files or symbols are known, prefer RoslynKit or `atlas-csharp-mapper`.
- Read tests before implementation when available.
- Prefer symbol and line-range reads over full-file reads.
- Stop after five source files and state a hypothesis before reading more.
- Feature cards are the only hand-maintained Atlas routing layer.
- Atlas does not store file, project, test, symbol, reference, or source-slice inventories; use `git ls-files`, `rg`, RoslynKit live queries, build/test output, or direct file inspection for current facts.
- Keep `repo-map.md`, `test-index.md`, and feature cards focused on durable architecture, source-to-test routing, and feature ownership facts.
- Update feature cards only with durable discoveries.
- When a task changes durable Atlas facts for a covered feature or domain, update the matching feature card and refresh `Last verified` before finishing.
- When durable repo shape or source-to-test mapping facts change, update `.codex/atlas/repo-map.md` or `.codex/atlas/test-index.md` in the same change.
- When Atlas workflow, routing scripts, or feature-card schema changes, update `.codex/atlas/README.md`, `.codex/atlas/USAGE.md`, or `.codex/atlas/feature-cards/README.md` in the same change.
- Use PowerShell scripts under `.codex/atlas/scripts/` for Atlas routing.

## State-Changing Git Command Safety

Git read commands such as `git status`, `git log`, `git diff`, `git show`, and `git branch --show-current` are allowed for inspection.

Do not run state-changing Git commands unless the user explicitly asks for that exact action in the current task, or unless permission is requested in chat and approval is received first. State-changing Git commands include `git commit`, `git push`, `git merge`, `git rebase`, `git cherry-pick`, `git checkout -b`, `git switch -c`, `git tag`, `git reset`, `git revert`, `git stash`, branch deletion, and any command that changes refs, the index, or the working tree.

Before any commit or push:

- verify there is a real diff, not just stat or line-ending noise;
- inspect staged, unstaged, and untracked files;
- stage only intended files unless the user explicitly asks to stage all files;
- re-check branch, upstream, and `git status`;
- stop cleanly if the tree is already synced.

## Coding Style & Naming Conventions

Follow `.editorconfig`: UTF-8, spaces, final newline, 4-space indentation for C# and PowerShell, 2-space indentation for XML/JSON/YAML. C# uses file-scoped namespaces, nullable reference types, implicit usings, latest language version, and warnings as errors. Use `Cli`, not `CLI`, in C# identifiers. Keep command output deterministic and markdown-first.

## C# Working Rules

Follow the existing style in touched files. Prefer clear names and structure over commentary. Use sparse XML documentation comments in C#: add a brief `summary` comment to each class, do not add comments or XML docs to constructors, add a brief `summary` comment to a public method only when its behavior is complex or non-obvious, and do not add parameter documentation comments.

Navigation comments should help RoslynKit `quick-info` guide the next hop. Add or refine public-method summaries only for entrypoints, orchestration points, cross-boundary adapters, Roslyn workspace/symbol/position resolution boundaries, or helpers whose name alone does not explain when to jump there. Keep summaries architectural and specific; avoid generic comments such as "Executes the method" and avoid documenting every public member for coverage.

- Preserve the CLI-first architecture: no MCP server, no LSP client, no background daemon, and no editor-specific protocol coupling.
- Prefer direct Roslyn/MSBuild APIs over shelling out to editors, language servers, or IDEs.
- Prioritize read-only Roslyn intelligence: inspect, navigate, understand, and verify C# code before edit-producing workflows.
- If formatting, rename, or code-action features are added, return deterministic proposed edits before adding any source-mutating apply mode.
- Keep command execution separate from argument parsing.
- Keep the markdown output contract (`docs/markdown-output-format.md`) stable, deterministic, and covered by parser/renderer tests when changed.
- Pass `CancellationToken` through async Roslyn operations when available.
- If a public CLI option, command, output shape, package surface, or documented workflow changes, update `README.md` or the relevant docs in the same change.

## Post-Change Formatting

After C# coding work, run these commands before final build/test verification:

- `dotnet format whitespace .\RoslynKit.slnx --no-restore --verbosity minimal`
- `dotnet format style .\RoslynKit.slnx --no-restore --severity warn --verbosity minimal`

## Testing Guidelines

Tests use xUnit through Microsoft Testing Platform in `tests/RoslynKit.Tests`. Name test methods as behavior statements, for example `Parse_RejectsDuplicateOption`. Add parser/renderer tests for CLI contract changes and focused Roslyn execution tests when command behavior changes. Run `dotnet test .\RoslynKit.slnx` before publishing changes.

## Commit & Pull Request Guidelines

Recent history uses short imperative subjects and conventional prefixes such as `docs:` and `chore:`. Match the surrounding history and keep subjects imperative.

- Keep commits focused. Separate behavior changes, docs-only edits, generated output refreshes, and repo maintenance when practical.
- Before committing, confirm the diff is real and relevant. Do not commit CRLF-only or stat-only churn just because `git status` mentions a file.
- Pull requests should explain what command, parser behavior, Roslyn behavior, or docs surface changed.
- Note output contract impacts explicitly and list the exact validation commands that were run.
- For this CLI-first repo, command output snippets are usually more useful than screenshots.

## Security & Configuration Tips

Do not commit secrets, local credentials, generated caches, package outputs, or accidental binaries.

Treat every repository listed in `docs/local-repository-reference.md` as a strict read-only reference while working in RoslynKit. Never suggest making changes in any of those reference repositories as part of a RoslynKit task, even when issues, gaps, or possible improvements are noticed there. Keep all recommended changes scoped to RoslynKit unless the user explicitly changes the task to one of those repositories.
