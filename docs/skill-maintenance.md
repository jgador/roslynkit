# RoslynKit Skill Maintenance

RoslynKit keeps two checked-in agent skills:

- `.agents\skills\roslynkit\SKILL.md` for the stable global `roslynkit` command.
- `.agents\skills\roslynkit-dev\SKILL.md` for the repo-default RoslynKit development workflow.

The two skill files should stay structurally aligned. They describe the same RoslynKit command surface and should diverge only where the invocation path is intentionally different.

## Ownership

Keep the ownership boundaries explicit:

- `AGENTS.md` is the source of truth for which skill is the default route in this repo.
- `.agents\skills\roslynkit-dev\SKILL.md` is usage-only guidance for Codex when the dev tool is already installed.
- `docs/dev-install.md` is the install/update source of truth for the side-by-side prerelease tool.

## Intentional differences

- The front matter `name` and `description`.
- The command prefix:
  - stable skill uses `roslynkit ...`
  - dev skill uses `& $roslynkitDev ...`

Do not hardcode a literal prerelease version inside `.agents\skills\roslynkit-dev\SKILL.md`. Updating from one prerelease to the next should normally require rerunning the install script, not editing the skill file.

## When to update both skill files

Update both skill files together when any of these change:

- the public CLI command names;
- required options such as `--target`, `--file`, or `--document-key`;
- recommended command ordering such as “run `workspace` first”;
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
