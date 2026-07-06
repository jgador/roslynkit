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

## Main workflows

- Print tool metadata with `version` or top-level `--version`.
- Enumerate workspace documents with `workspace`, including generated, additional, or analyzer-config documents when requested.
- Search, navigate, and inspect C# symbols with commands such as `symbols`, `definition`, `references`, `quick-info`, and `symbol-source`.
- Read resolved documents with `document-text`, `document-lines`, or `document-symbols`.

Document commands use `--file <path>` as the document selector. Relative paths resolve from the current working directory, and ambiguous linked or multi-targeted files can be narrowed with `--project`, `--tfm`, or `--document-kind`.

## Example

```powershell
roslynkit workspace --target .\MySolution.slnx
```

See the repository README for usage guidance and the generated runtime command reference. Side-by-side prerelease dev installs live in `docs/dev-install.md`, and maintainer packaging steps live in `docs/dotnet-tool-release.md` in the same repository:

[RoslynKit on GitHub](https://github.com/jgador/roslynkit)
