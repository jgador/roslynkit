# Repository Guidelines

## Canonical Docs

Use one source of truth per topic:

- [README.md](README.md): user-facing overview, output format, packaging, and quick-start commands.
- [docs/agents/roslynkit-command-reference.md](docs/agents/roslynkit-command-reference.md): generated runtime command reference from `BuiltinCommandRegistry`; regenerate with `dotnet run --file .\tools\RoslynKit.CommandDocs.cs -- --write`.
- [docs/markdown-output-format.md](docs/markdown-output-format.md): shared command output contract for humans and agents.
- [docs/dev-install.md](docs/dev-install.md): semi-manual side-by-side prerelease installation for RoslynKit development.
- [docs/dotnet-tool-release.md](docs/dotnet-tool-release.md): maintainer packaging and release workflow.
- [docs/agents/README.md](docs/agents/README.md): index for coding-agent workflow docs that agents may discover and apply on their own.
- [docs/agents/roslynkit-command-reference.md](docs/agents/roslynkit-command-reference.md): generated command facts agents can use for exact runtime command names, usage strings, and options.
- [docs/agents/skill-maintenance.md](docs/agents/skill-maintenance.md): ownership and synchronization rules for checked-in RoslynKit skill files.
- [docs/local-repository-reference.md](docs/local-repository-reference.md): user-owned local repository reference map; use only when explicitly named or tagged by the user.
- [docs/roslyn-lsp-commands.md](docs/roslyn-lsp-commands.md): human-facing Roslyn language-server inventory and RoslynKit planning coverage; do not use for current command routing.
- [docs/token-efficiency-benchmark.md](docs/token-efficiency-benchmark.md): manual benchmark procedure; use only when the user asks to measure token efficiency.
- [AGENTS.md](AGENTS.md): active coding-agent rules, safety rules, and repo workflow expectations.

Keep this file focused on the rules agents need during execution. Put longer agent guidance under `docs/agents/`, and do not restate full command reference material here.

## Project Structure & Module Organization

RoslynKit is a .NET 10 command-line tool. Production code lives under `src/RoslynKit`, with the console entrypoint in `Program.cs`, CLI parsing in `CliParser.cs`, command metadata in `BuiltinCommandRegistry.cs`, and Roslyn execution logic in `RoslynCommandExecutor.cs`. Tests live under `tests/RoslynKit.Tests`, repo-local test-side utilities live under `tests/`, and small repo tooling lives under `tools/`. Shared product docs live under `docs/`; coding-agent support docs live under `docs/agents/`. Manual reference, roadmap, and benchmark docs live directly under `docs/` and are opt-in unless the user names them.

## Build, Test, and Development Commands

- `dotnet restore .\RoslynKit.slnx` restores packages for the solution.
- `dotnet build .\RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"` builds the CLI and tests with concise output.
- `dotnet test .\RoslynKit.slnx` runs the xUnit test suite through Microsoft Testing Platform.
- `dotnet run --project .\src\RoslynKit -- help` runs the CLI locally.
- `dotnet run --file .\tools\RoslynKit.CommandDocs.cs -- --check` verifies the generated command reference is in sync with runtime command metadata.
- `dotnet pack .\src\RoslynKit\RoslynKit.csproj` creates the .NET tool package.

## Agent Workflow

Verify conclusions against current files, tests, docs, and command output.

When adding or editing prose in Markdown docs, checked-in agent prompts, or skill files, write repo file references as Markdown links with the path as the link label, such as [docs/agents/README.md](docs/agents/README.md). Use code formatting only for non-file literals, globs, command arguments, generated output, or paths where Markdown links would change behavior.

When writing or editing agent-facing prose, avoid second-person pronouns for coding agents. Do not use `you` or `your` to refer to an agent, sub-agent, coding tool, or future agent reader; use explicit nouns such as `the agent`, `the sub-agent`, `Codex`, or `coding agents` instead.

For exact runtime command names, usage strings, and options, read generated [docs/agents/roslynkit-command-reference.md](docs/agents/roslynkit-command-reference.md). Keep agent guidance concise and route-oriented; do not duplicate full command reference material in [AGENTS.md](AGENTS.md) or skill files.

For longer agent-only guidance, start at [docs/agents/README.md](docs/agents/README.md).

Do not read [docs/local-repository-reference.md](docs/local-repository-reference.md), [docs/roslyn-lsp-commands.md](docs/roslyn-lsp-commands.md), or [docs/token-efficiency-benchmark.md](docs/token-efficiency-benchmark.md) by default. Use those docs only when the user explicitly names or tags them, or when the task is specifically about local reference repositories, RoslynKit roadmap coverage, or token-efficiency measurement.

Do not use Repository Synapse in this repository. Do not run `synapse ensure`, `synapse recall`, `synapse tests`, or any other command that creates `.synapse/` repo-local cache files. Use Atlas, RoslynKit, scout agents, and direct file/test inspection instead.

For ad-hoc scripts, scratch files, and temporary command results, use a clear subfolder under `artifacts/` such as `artifacts/<task-name>/`. Do not create repo-root scratch folders such as `.tmp`; `artifacts/` is ignored and is the expected place for disposable local outputs.

### RoslynKit Default Semantic Inspection

When the task is ordinary C# semantic inspection inside this RoslynKit repo, use [.agents/skills/roslynkit-dev/SKILL.md](.agents/skills/roslynkit-dev/SKILL.md) first. Treat that dev skill as the repo-default route for declarations, symbol structure, definitions, references, implementations, types, signatures, generated documents, and similar Roslyn-backed inspection work.

Use [.agents/skills/roslynkit/SKILL.md](.agents/skills/roslynkit/SKILL.md) only when the task is explicitly about the stable global tool behavior or the released stable workflow.

For literal search, prose inspection, non-C# files, or RoslynKit workspace-load failures, fall back to the terminal-native tool for the current platform instead of forcing RoslynKit.

### No-Primer C# Tool Gate

When the task does not already confirm C#/.NET from the user prompt, named files, project/solution files, or known C# symbols, do not ask whether to use RoslynKit and do not mention RoslynKit in the classifier. First separate code investigation from C#/.NET confirmation with this no-primer shape:

```json
{"code_investigation":"yes|no|unknown","csharp_dotnet_confirmed":"yes|no|unknown","confidence":"low|medium|high","reason":"one sentence","next_question_if_unknown":"one question or null"}
```

- If `csharp_dotnet_confirmed=yes`, RoslynKit may be used once a symbol, file, project, or cursor position is known.
- If `code_investigation=yes` and `csharp_dotnet_confirmed=unknown`, do not use RoslynKit yet; ask `next_question_if_unknown` or gather minimal repo evidence first.
- Minimal repo evidence for C#/.NET is a lightweight discovery of `.sln`, `.slnx`, `.csproj`, `global.json` with .NET SDK context, or relevant `.cs` source/test files.
- If `code_investigation=no`, do not use RoslynKit first.
- Stop after at most five classifier or clarification exchanges; if C#/.NET is still unknown, proceed with non-RoslynKit narrowing first.

### Scout-First Repo Search

Use the `scout` sub-agent for repo discovery when the current agent environment exposes it and any of these are true:

- the user asks to find, trace, investigate, or discover which files matter;
- the user did not name an exact target file;
- more than one top-level area may be relevant;
- the likely read set is more than 3 files.

Skip `scout` when one obvious target file is already known or the task is a single-file explanation or edit.

When using `scout`:

- ground first in [AGENTS.md](AGENTS.md), [README.md](README.md), and any directly named files; include [docs/agents/README.md](docs/agents/README.md) for agent workflow or skill-maintenance tasks;
- normalize and de-overlap scopes before spawning;
- spawn one `scout` per disjoint scope;
- prefer these default scopes when the area is unclear: `src/RoslynKit/`, `tests/RoslynKit.Tests/`, `docs/`, repo-root config files, and `.agents/` or `.codex/` if agent packaging is involved.

Every scout prompt must include `assigned_scope`, `search_goal`, known keywords or filenames, and the required response format. Every scout must return `assigned_scope`, `files_examined`, `likely_relevant_files`, `evidence`, `handoff_paths`, `short_summary`, and `confidence`. After scouts return, inspect `likely_relevant_files` locally before deeper tracing.

## Repository Atlas Reading Policy

- Use [.codex/atlas/repo-map.md](.codex/atlas/repo-map.md) before broad source reading.
- When [.codex/atlas/repo-map.md](.codex/atlas/repo-map.md) contains a runtime or architecture spine for the task domain, convert that spine into the first read order before broad literal search or scout discovery.
- Use `atlas-router` when the architecture/domain is unclear.
- Use `scout` when files are unclear inside a bounded scope.
- For C# semantic inspection after candidate files or symbols are known, prefer RoslynKit or `atlas-csharp-mapper`.
- Read tests before implementation when available.
- Prefer symbol and line-range reads over full-file reads.
- Stop after five source files and state a hypothesis before reading more.
- Atlas does not store file, project, test, symbol, reference, or source-slice inventories; use `git ls-files`, `rg`, RoslynKit live queries, build/test output, or direct file inspection for current facts.
- Keep [.codex/atlas/repo-map.md](.codex/atlas/repo-map.md) focused on durable architecture, source-to-test routing, feature ownership facts, and navigation rules.
- When durable Atlas facts change, update [.codex/atlas/repo-map.md](.codex/atlas/repo-map.md) and refresh its `Last verified` date before finishing.
- When Atlas workflow changes, update this policy and the relevant `.codex/agents/*.toml` prompts in the same change.

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

Navigation comments should help RoslynKit documentation-enabled navigation output guide the next hop. Add or refine public-method summaries only for entrypoints, orchestration points, cross-boundary adapters, Roslyn workspace/symbol/position resolution boundaries, or helpers whose name alone does not explain when to jump there. Keep summaries architectural and specific; avoid generic comments such as "Executes the method" and avoid documenting every public member for coverage.

- Keep C# files under 1000 lines of code when practical. This is guidance, not a hard rule: when a `.cs` file grows beyond 1000 lines, consider whether it mixes concerns or has a natural refactor seam before splitting it.
- Preserve the CLI-first architecture: no MCP server, no LSP client, no background daemon, and no editor-specific protocol coupling.
- Prefer direct Roslyn/MSBuild APIs over shelling out to editors, language servers, or IDEs.
- Prioritize read-only Roslyn intelligence: inspect, navigate, understand, and verify C# code before edit-producing workflows.
- If formatting, rename, or code-action features are added, return deterministic proposed edits before adding any source-mutating apply mode.
- Keep command execution separate from argument parsing.
- Keep the markdown output contract ([docs/markdown-output-format.md](docs/markdown-output-format.md)) stable, deterministic, and covered by parser/renderer tests when changed.
- Pass `CancellationToken` through async Roslyn operations when available.
- If a public CLI option, command, output shape, package surface, or documented workflow changes, update [README.md](README.md) or the relevant docs in the same change. When command metadata changes, regenerate [docs/agents/roslynkit-command-reference.md](docs/agents/roslynkit-command-reference.md) and verify it with `dotnet run --file .\tools\RoslynKit.CommandDocs.cs -- --check`.

## Post-Change Formatting

After C# coding work, run these commands before final build/test verification:

- `dotnet format whitespace .\RoslynKit.slnx --no-restore --verbosity minimal`
- `dotnet format style .\RoslynKit.slnx --no-restore --severity warn --verbosity minimal`

## End-of-Session Commit Context

After a meaningful coding session, refresh [artifacts/commit-context.md](artifacts/commit-context.md) as a commit-ready message by following [.agents/skills/commit-context/SKILL.md](.agents/skills/commit-context/SKILL.md).

Run this like the post-change formatting step: do it near the end of the session and before `$git-commit-push`, using live git status and diffs to describe the whole current change set, not only the latest edit. Match the recent commit message structure: a Conventional Commit subject, a blank line, and concise imperative body paragraphs. Treat the file as ignored advisory context; commit/push workflows must still re-check staged, unstaged, and untracked changes.

## Testing Guidelines

Tests use xUnit through Microsoft Testing Platform in `tests/RoslynKit.Tests`. Name test methods as behavior statements, for example `Parse_RejectsDuplicateOption`. Add parser/renderer tests for CLI contract changes and focused Roslyn execution tests when command behavior changes. Run `dotnet test .\RoslynKit.slnx` before publishing changes.

## Commit & Pull Request Guidelines

Recent history uses short imperative subjects and conventional prefixes such as `docs:` and `chore:`. Match the surrounding history and keep subjects imperative.

- When the user invokes `$git-commit-push`, use [.agents/skills/git-commit-push/SKILL.md](.agents/skills/git-commit-push/SKILL.md): stage all non-ignored changes with `git add -A`, commit with [artifacts/commit-context.md](artifacts/commit-context.md), and push non-interactively after the required preflight checks.
- Keep commits focused. Separate behavior changes, docs-only edits, generated output refreshes, and repo maintenance when practical.
- Before committing, confirm the diff is real and relevant. Do not commit CRLF-only or stat-only churn just because `git status` mentions a file.
- Pull requests should explain what command, parser behavior, Roslyn behavior, or docs surface changed.
- Note output contract impacts explicitly and list the exact validation commands that were run.
- For this CLI-first repo, command output snippets are usually more useful than screenshots.

## Security & Configuration Tips

Do not commit secrets, local credentials, generated caches, package outputs, or accidental binaries.

If the task explicitly uses [docs/local-repository-reference.md](docs/local-repository-reference.md), treat every repository listed there as a strict read-only reference while working in RoslynKit. Never suggest making changes in any of those reference repositories as part of a RoslynKit task, even when issues, gaps, or possible improvements are noticed there. Keep all recommended changes scoped to RoslynKit unless the user explicitly changes the task to one of those repositories.
