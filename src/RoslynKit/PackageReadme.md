# roslynkit

`roslynkit` is a .NET tool for deterministic, read-only Roslyn-powered C# code intelligence in terminal and coding-agent workflows.

## Install from NuGet.org

```powershell
dotnet tool install --global roslynkit
roslynkit version
```

To update an existing install:

```powershell
dotnet tool update --global roslynkit
```

## Install from a local folder feed

```powershell
dotnet tool install --global roslynkit --add-source <local-feed-path> --version <version> --ignore-failed-sources
roslynkit version
```

To update an existing local install:

```powershell
dotnet tool update --global roslynkit --add-source <local-feed-path> --version <version> --ignore-failed-sources
```

## Main commands

- `version`, `--version`: print the installed RoslynKit version
- `workspace`: enumerate repo-relevant source documents and opt into generated, additional, or analyzer-config documents
- `symbols`: search the target workspace for named types or members
- `document-symbols`: inspect the structure of one document
- `document-text`: read a full file-backed or generated document without shelling out to text tools
- `definition`, `references`, `implementations`, `quick-info`, `type-definition`, `signature-help`: C# semantic inspection commands for source and source-generated documents

## Example

```powershell
roslynkit workspace --target .\MySolution.slnx
```

See the repository README for the full command reference and usage guide. Side-by-side prerelease dev installs live in `docs/dev-install.md`, and maintainer packaging steps live in `docs/dotnet-tool-release.md` in the same repository:

[RoslynKit on GitHub](https://github.com/jgador/roslynkit)
