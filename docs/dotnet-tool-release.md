# RoslynKit Dotnet Tool Packaging

Run every command from the repo root.

## What gets produced

RoslynKit currently produces one .NET tool package:

- `roslynkit`

The release version comes from `Directory.Build.props`. The public package metadata lives in `src/RoslynKit/RoslynKit.csproj`, and the NuGet package readme lives in `src/RoslynKit/PackageReadme.md`.

## 1. Update package metadata

1. Set the new `<Version>` in `Directory.Build.props` using a bare NuGet version such as `0.1.0`. Use the leading `v` only for Git tags or release titles such as `v0.1.0`.
2. Confirm `src/RoslynKit/RoslynKit.csproj` still has the correct public package metadata: `PackageId` is `roslynkit`, `ToolCommandName` is `roslynkit`, and the repository URL, license, tags, and package readme values are still correct.
3. If the public CLI surface, repo-local skill workflow, or install story changed, update `README.md` in the same change.

## 2. Validate the repo before packing

Run the standard validation lane first:

```powershell
dotnet restore .\RoslynKit.slnx
dotnet build .\RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"
dotnet test .\RoslynKit.slnx
```

## 3. Build the local folder feed

Use the helper script:

```powershell
pwsh .\scripts\prepare-roslynkit-package.ps1
```

That script:

1. Resolves the repo root and `dotnet` executable.
2. Reads and validates `<Version>` from `Directory.Build.props`.
3. Recreates the local folder feed at `.\artifacts\packages\roslynkit`.
4. Packs `src\RoslynKit\RoslynKit.csproj` in `Release` into that folder feed.
5. Verifies that `roslynkit.<version>.nupkg` exists.
6. Prints the exact `dotnet tool install` and `dotnet tool update` commands for dogfooding the current checkout.

If you want the raw command instead of the helper script, this is the equivalent pack step:

```powershell
dotnet pack .\src\RoslynKit\RoslynKit.csproj -c Release -o .\artifacts\packages\roslynkit
```

## 4. Dogfood the local package

Install from the local folder feed into the standard global tool location such as `%USERPROFILE%\.dotnet\tools`:

```powershell
dotnet tool install --global roslynkit --add-source .\artifacts\packages\roslynkit --version <version> --ignore-failed-sources
roslynkit help
```

If `roslynkit` is already installed globally, update it in place:

```powershell
dotnet tool update --global roslynkit --add-source .\artifacts\packages\roslynkit --version <version> --ignore-failed-sources
roslynkit help
```

## 5. Publish later if needed

When you are ready to push a public package, upload the `.nupkg` from `.\artifacts\packages\roslynkit` or run `dotnet nuget push` against that file.

Do not reuse a version number after a bad package. Fix the repo, bump `<Version>`, rebuild the package, and publish a new version instead.
