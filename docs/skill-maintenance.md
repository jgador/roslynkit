# RoslynKit Skill Maintenance

RoslynKit keeps two checked-in agent skills:

- `.agents\skills\roslynkit\SKILL.md` for the stable global `roslynkit` command.
- `.agents\skills\roslynkit-dev\SKILL.md` for the side-by-side prerelease RoslynKit dev tool.

The two skill files should stay structurally aligned. They describe the same RoslynKit command surface and should diverge only where the invocation path is intentionally different.
Their normative guidance and examples must stay repository-agnostic so the same checked-in files can be reused in arbitrary C# repositories.

## Ownership

Keep the ownership boundaries explicit:

- `AGENTS.md` is the source of truth for which skill is the default route in this repo and for repo-specific workflow policy.
- `.codex\atlas\` is the home for durable repo-specific tracing or routing guidance when Atlas coverage exists.
- `.agents\skills\roslynkit\SKILL.md` and `.agents\skills\roslynkit-dev\SKILL.md` are reusable usage-only guides. Do not embed repo-owned routing sequences, repo-local source paths, or project-specific symbol chains inside either skill.
- `docs/dev-install.md` is the install/update source of truth for the side-by-side prerelease tool.

## Intentional differences

- The front matter `name` and `description`.
- The command prefix:
  - stable skill uses `roslynkit ...`
  - dev skill uses `& $roslynkitDev ...`
- The dev-skill reference to `docs/dev-install.md`.

Do not hardcode a literal prerelease version inside `.agents\skills\roslynkit-dev\SKILL.md`. Updating from one prerelease to the next should normally require rerunning the install script, not editing the skill file.

## When to update both skill files

Update both skill files together when any of these change:

- the public CLI command names;
- required options such as `--target`, `--file`, `--project`, `--tfm`, or `--document-kind`;
- recommended command ordering such as “run `workspace` first”;
- reusable cursor-choice guidance or generic examples;
- fallback guidance for non-C# or non-semantic tasks.

When you edit one skill file for command-shape guidance, mirror the same structural change in the other skill file in the same commit.

## Stable skill workflow

The stable skill assumes `roslynkit` is available globally:

```powershell
dotnet tool install --global roslynkit
dotnet tool update --global roslynkit
```

If the stable global install story changes, update:

- `.agents\skills\roslynkit\SKILL.md`
- `README.md`
- `src\RoslynKit\PackageReadme.md` if the public install story changed

## Dev install workflow

The side-by-side prerelease install is documented in `docs/dev-install.md`.

If the dev tool path or install script contract changes, update:

- `.agents\skills\roslynkit-dev\SKILL.md`
- `AGENTS.md` if the repo-default route changed
- `docs\dev-install.md`
- `scripts\install-roslynkit-dev.ps1`
- `README.md` when the user-facing install story changed
- `docs\dotnet-tool-release.md` when the maintainer packaging story changed

## Version updates

Stable versions and prerelease versions are maintained differently:

- Stable version updates usually change package examples in `README.md`, `docs\dotnet-tool-release.md`, and `src\RoslynKit\PackageReadme.md`.
- Prerelease dogfooding usually does not require any skill-file text change unless the install path or invocation pattern changed.

Use a bare stable version such as `0.1.0` for global install examples. Use a prerelease such as `0.1.1-dev.1` for the side-by-side dev tool path.
