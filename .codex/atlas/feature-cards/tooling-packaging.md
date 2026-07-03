# Tooling And Packaging

## Purpose

Package, install, and maintain stable and dev RoslynKit tool flows plus checked-in skills.

## Task keywords

- packaging and release
- install and tool paths
- nupkg and pack
- dev install
- skill maintenance

## Entrypoints

- `scripts/prepare-roslynkit-package.ps1`
- `scripts/install-roslynkit-dev.ps1`
- `scripts/RoslynKit.Packaging.ps1`
- `src/RoslynKit/RoslynKit.csproj`
- `docs/dev-install.md`
- `docs/dotnet-tool-release.md`
- `docs/skill-maintenance.md`

## Important symbols

- `ToolCommandName`
- `PackageId`
- `Version`
- `UseArtifactsOutput`

## Important files

- `scripts/prepare-roslynkit-package.ps1`
- `scripts/install-roslynkit-dev.ps1`
- `scripts/RoslynKit.Packaging.ps1`
- `src/RoslynKit/RoslynKit.csproj`
- `docs/dev-install.md`
- `docs/dotnet-tool-release.md`
- `docs/skill-maintenance.md`

## Nearest tests

- `tests/RoslynKit.Tests/CliOutputTests.cs`

## Build/test commands

- `dotnet build .\RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"`
- `dotnet test .\RoslynKit.slnx`
- `.\scripts\prepare-roslynkit-package.ps1`

## Invariants

- Package ID stays lowercase `roslynkit`.
- Stable package feed lives under `artifacts\packages\roslynkit`.
- Side-by-side dev install lives under `$HOME\.roslynkit\tools\roslynkit-dev`.

## Common pitfalls

- Mixing stable and dev install paths.
- Updating commands, docs, and skills out of sync.

## Do not read first

- `src/RoslynKit/RoslynCommandExecutor.cs`
- `tests/RoslynKit.WorkspaceGraphDump/Program.cs`

## Last verified

`2026-07-03`
