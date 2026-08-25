# RoslynKit Skill Maintenance

This is a coding-agent workflow document. Root [AGENTS.md](../../AGENTS.md) owns active repo policy; this file owns the longer synchronization rules for reusable skill files and init-scaffolded bundles.

RoslynKit keeps one canonical stable command skill bundle:

- [.agents/skills/roslynkit/SKILL.md](../../.agents/skills/roslynkit/SKILL.md): stable global `roslynkit` command workflow.
- [.agents/skills/roslynkit/references/commands.md](../../.agents/skills/roslynkit/references/commands.md): generated runtime command reference.
- [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md): shared output contract.

[.agents/skills/roslynkit-dev/SKILL.md](../../.agents/skills/roslynkit-dev/SKILL.md) is the repo-local side-by-side prerelease RoslynKit dev tool guide. It shares the stable bundle references instead of duplicating command and output contract docs.

The stable bundle and dev skill should stay structurally aligned. They describe the same RoslynKit command surface and should diverge only where the invocation path is intentionally different. Normative guidance and examples must stay repository-agnostic so the stable bundle can be scaffolded into arbitrary C# repositories.

The separate [.agents/skills/commit-context/SKILL.md](../../.agents/skills/commit-context/SKILL.md), [.agents/skills/git-commit-push/SKILL.md](../../.agents/skills/git-commit-push/SKILL.md), [.agents/skills/security-audit/SKILL.md](../../.agents/skills/security-audit/SKILL.md), and [.agents/skills/benchmark/SKILL.md](../../.agents/skills/benchmark/SKILL.md) files are standalone repo workflow skills. They maintain ignored local commit notes, commit from that prepared context, run read-only repository security audits, and run explicitly requested native raw-text versus RoslynKit text-only benchmarks. They are not part of the stable/dev RoslynKit command-skill pair, do not need to mirror either RoslynKit skill, and must not be added to the `roslynkit init` bundle.

[.agents/skills/grill-me/SKILL.md](../../.agents/skills/grill-me/SKILL.md) is a self-contained repo-local productivity skill intended for explicit invocation and adapted from the upstream [grill-me](https://github.com/mattpocock/skills/tree/main/skills/productivity/grill-me) wrapper and [grilling](https://github.com/mattpocock/skills/tree/main/skills/productivity/grilling) primitive. It is not part of the stable RoslynKit bundle.

## Agent-Specific Scaffolding

`roslynkit init` embeds the canonical [.agents/skills/roslynkit/](../../.agents/skills/roslynkit/) bundle at pack time and scaffolds the same files to the selected agent root:

- `codex` -> `.agents/skills/roslynkit/`
- `claude` -> `.claude/skills/roslynkit/`
- `copilot` -> `.github/skills/roslynkit/`

Do not check in `.claude/skills/roslynkit/` or `.github/skills/roslynkit/` duplicates in this repository. Add scripts, references, or other future bundle files only under [.agents/skills/roslynkit/](../../.agents/skills/roslynkit/); the init command preserves every bundle-relative path when scaffolding.

## Ownership

Keep the ownership boundaries explicit:

- [AGENTS.md](../../AGENTS.md) is the source of truth for which skill is the default route in this repo and for repo-specific workflow policy.
- `.codex\atlas\` is the home for durable repo-specific tracing or routing guidance when Atlas coverage exists.
- [.agents/skills/roslynkit/SKILL.md](../../.agents/skills/roslynkit/SKILL.md) and [.agents/skills/roslynkit-dev/SKILL.md](../../.agents/skills/roslynkit-dev/SKILL.md) are reusable usage-only guides. Do not embed repo-owned routing sequences, repo-local source paths, or project-specific symbol chains inside either skill.
- [.agents/skills/roslynkit/references/commands.md](../../.agents/skills/roslynkit/references/commands.md) and [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md) are shared stable-bundle references and must remain suitable for installed copies in other repositories.
- [.agents/skills/commit-context/SKILL.md](../../.agents/skills/commit-context/SKILL.md) and [.agents/skills/git-commit-push/SKILL.md](../../.agents/skills/git-commit-push/SKILL.md) are repo-specific workflow guides and may reference [artifacts/commit-context.md](../../artifacts/commit-context.md) and RoslynKit commit policy directly.
- [.agents/skills/security-audit/SKILL.md](../../.agents/skills/security-audit/SKILL.md) is a repository-agnostic read-only audit workflow; keep its prompt template and scan commands free of RoslynKit-specific paths so it stays reusable in other repositories.
- [.agents/skills/benchmark/SKILL.md](../../.agents/skills/benchmark/SKILL.md) is a standalone explicit-only workflow skill for the Bash-controlled native benchmark. [scripts/benchmark.sh](../../scripts/benchmark.sh) forwards options to the helper, schedules from the helper's control directive, and makes the direct `codex exec` calls; the C# helper owns option parsing, defaults, validation, case validation, retrieval, JSON Lines (JSONL) accounting, evidence validation, persistence, and reports. Keep the skill concise and route-oriented, and keep it outside the stable RoslynKit bundle so `roslynkit init` does not scaffold it.
- [.agents/skills/grill-me/SKILL.md](../../.agents/skills/grill-me/SKILL.md) keeps the upstream interview behavior inline without agent-specific metadata.
- [docs/dev-install.md](../dev-install.md) is the install/update source of truth for the side-by-side prerelease tool.

## Intentional differences

- The front matter `name` and `description`.
- The command prefix:
  - stable skill uses `roslynkit ...`
  - dev skill uses `& $roslynkitDev ...`
- The dev-skill reference to [docs/dev-install.md](../dev-install.md).

Do not hardcode a literal prerelease version inside [.agents/skills/roslynkit-dev/SKILL.md](../../.agents/skills/roslynkit-dev/SKILL.md). Updating from one prerelease to the next should normally require rerunning the install script, not editing the skill file.

## When to update both skill files

Update both skill files together when any of these change:

- the public CLI command names;
- required options such as `--target`, `--file`, `--project`, `--tfm`, or `--document-kind`;
- recommended command ordering such as “run `workspace` first”;
- reusable cursor-choice guidance or generic examples;
- fallback guidance for non-C# or non-semantic tasks.

Exact runtime command names, usage strings, and options are generated in [.agents/skills/roslynkit/references/commands.md](../../.agents/skills/roslynkit/references/commands.md) from `BuiltinCommandRegistry`. When command metadata changes, regenerate that file and keep the skills focused on compact routing guidance instead of duplicating the full reference.

When one skill file changes for command-shape guidance, mirror the same structural change in the other skill file in the same commit.

## Stable skill workflow

The stable skill assumes `roslynkit` is available globally:

```powershell
dotnet tool install --global roslynkit
dotnet tool update --global roslynkit
```

If the stable global install story changes, update:

- [.agents/skills/roslynkit/SKILL.md](../../.agents/skills/roslynkit/SKILL.md)
- [README.md](../../README.md)
- [src/RoslynKit/PackageReadme.md](../../src/RoslynKit/PackageReadme.md) if the public install story changed

## Dev install workflow

The side-by-side prerelease install is documented in [docs/dev-install.md](../dev-install.md).

If the dev tool path or install script contract changes, update:

- [.agents/skills/roslynkit-dev/SKILL.md](../../.agents/skills/roslynkit-dev/SKILL.md)
- [AGENTS.md](../../AGENTS.md) if the repo-default route changed
- [docs/dev-install.md](../dev-install.md)
- `scripts\install-roslynkit-dev.ps1`
- [README.md](../../README.md) when the user-facing install story changed
- [docs/dotnet-tool-release.md](../dotnet-tool-release.md) when the maintainer packaging story changed

## Version updates

Stable versions and prerelease versions are maintained differently:

- Stable version updates usually change package examples in [README.md](../../README.md), [docs/dotnet-tool-release.md](../dotnet-tool-release.md), and [src/RoslynKit/PackageReadme.md](../../src/RoslynKit/PackageReadme.md).
- Prerelease dogfooding usually does not require any skill-file text change unless the install path or invocation pattern changed.

Use a bare stable version such as `0.2.0` for global install examples. Use a prerelease such as `0.2.0-dev.1` for the side-by-side dev tool path.
