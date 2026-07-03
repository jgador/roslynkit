[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    $gitRoot = & git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and $gitRoot) {
        return [IO.Path]::GetFullPath($gitRoot.Trim())
    }

    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
}

function Normalize-RepoPath {
    param(
        [string]$RepoRoot,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    $resolvedRoot = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\')

    try {
        $resolvedPath = [IO.Path]::GetFullPath($Path)
    }
    catch {
        return $Path.Replace('\', '/')
    }

    if ($resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $resolvedPath.Substring($resolvedRoot.Length).TrimStart('\').Replace('\', '/')
    }

    return $Path.Replace('\', '/')
}

function Should-IgnorePath {
    param([string]$RelativePath)

    $path = $RelativePath.Replace('\', '/')

    if ($path -match '^(artifacts/|TestResults/|\.vs/|Visual Studio 18/)') {
        return $true
    }

    if ($path -match '(^|/)(bin|obj)/') {
        return $true
    }

    if ($path -match '^\.synapse/(graph\.json|memories\.md|synapse-memory\.json)$') {
        return $true
    }

    if ($path -match '^\.codex/atlas/indexes/.*\.json$') {
        return $true
    }

    if ($path -match '\.nupkg$') {
        return $true
    }

    return $false
}

function Get-FileArea {
    param([string]$RelativePath)

    switch -Wildcard ($RelativePath.Replace('\', '/')) {
        '.codex/atlas/*' { return 'atlas' }
        '.codex/*' { return 'codex' }
        '.agents/*' { return 'agents' }
        'src/*' { return 'src' }
        'tests/*' { return 'tests' }
        'docs/*' { return 'docs' }
        'scripts/*' { return 'scripts' }
        default {
            $firstSegment = ($RelativePath.Replace('\', '/') -split '/', 2)[0]
            if ([string]::IsNullOrWhiteSpace($firstSegment)) {
                return 'root'
            }

            return $firstSegment
        }
    }
}

function Get-FileCategory {
    param([string]$RelativePath)

    $path = $RelativePath.Replace('\', '/')

    if ($path -like 'src/*.cs') { return 'source' }
    if ($path -like 'tests/RoslynKit.Tests/*.cs') { return 'test' }
    if ($path -like 'tests/RoslynKit.WorkspaceGraphDump/*') { return 'test-support' }
    if ($path -like 'tests/FixtureWorkspace/*') { return 'fixture' }
    if ($path -like 'docs/*') { return 'doc' }
    if ($path -like 'scripts/*.ps1') { return 'script' }
    if ($path -like '.codex/agents/*.toml') { return 'agent' }
    if ($path -like '.agents/skills/*') { return 'skill' }
    if ($path -like '.codex/atlas/*') { return 'atlas' }
    if ($path -match '(^|/)(README|AGENTS)\.md$') { return 'doc' }
    if ($path -match '\.(csproj|slnx|props|json|toml|md|ps1)$') { return 'config' }
    return 'other'
}

function Resolve-RoslynKitCommand {
    $repoBuild = Join-Path (Join-Path (Join-Path (Join-Path $PSScriptRoot '..\..\..') 'artifacts\bin\RoslynKit') 'debug') ($(if ($IsWindows) { 'roslynkit.exe' } else { 'roslynkit' }))
    if (Test-Path -LiteralPath $repoBuild) {
        return [PSCustomObject]@{
            Name = 'roslynkit-repo-build'
            Path = (Resolve-Path -LiteralPath $repoBuild).Path
        }
    }

    $stable = Get-Command roslynkit -ErrorAction SilentlyContinue
    if ($stable) {
        return [PSCustomObject]@{
            Name = 'roslynkit'
            Path = $stable.Source
        }
    }

    $devRoot = Join-Path (Join-Path (Join-Path $HOME '.roslynkit') 'tools') 'roslynkit-dev'
    $devPath = Join-Path $devRoot ($(if ($IsWindows) { 'roslynkit.exe' } else { 'roslynkit' }))
    if (Test-Path -LiteralPath $devPath) {
        return [PSCustomObject]@{
            Name = 'roslynkit-dev'
            Path = $devPath
        }
    }

    return $null
}

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Data
    )

    $json = $Data | ConvertTo-Json -Depth 20
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
}

$repoRoot = Get-RepoRoot
$atlasRoot = Join-Path $repoRoot '.codex\atlas'
$indexRoot = Join-Path $atlasRoot 'indexes'
$solutionPath = Join-Path $repoRoot 'RoslynKit.slnx'
$generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')

Push-Location $repoRoot
try {
    $files = @(
        & git ls-files --cached --others --exclude-standard 2>$null |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not (Should-IgnorePath $_) } |
            Sort-Object -Unique
    )

    $fileIndex = [PSCustomObject]@{
        schemaVersion = 1
        generatedAtUtc = $generatedAtUtc
        repoRoot = $repoRoot
        files = @(
            foreach ($path in $files) {
                [PSCustomObject]@{
                    path = $path
                    extension = [IO.Path]::GetExtension($path)
                    area = Get-FileArea $path
                    category = Get-FileCategory $path
                }
            }
        )
    }
    Write-JsonFile -Path (Join-Path $indexRoot 'file-index.json') -Data $fileIndex
    Write-Host "Wrote file-index.json ($($fileIndex.files.Count) files)."

    $solutionProjects = @{}
    if (Test-Path -LiteralPath $solutionPath) {
        [xml]$solutionXml = Get-Content -LiteralPath $solutionPath -Raw
        foreach ($projectNode in @($solutionXml.SelectNodes('//Project'))) {
            $normalized = Normalize-RepoPath -RepoRoot $repoRoot -Path $projectNode.Path
            if ($normalized) {
                $solutionProjects[$normalized] = $true
            }
        }
    }

    $projectEntries = @(
        foreach ($projectPath in ($files | Where-Object { $_ -like '*.csproj' })) {
            [xml]$projectXml = Get-Content -LiteralPath (Join-Path $repoRoot $projectPath) -Raw

            $name = $null
            $assemblyNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/AssemblyName')
            if ($assemblyNode -and -not [string]::IsNullOrWhiteSpace($assemblyNode.InnerText)) {
                $name = $assemblyNode.InnerText.Trim()
            }

            $isTestProject = @(
                $projectXml.SelectNodes('/Project/PropertyGroup/IsTestProject') |
                    Where-Object { $_.InnerText.Trim().ToLowerInvariant() -eq 'true' }
            ).Count -gt 0

            if (-not $name) {
                $name = [IO.Path]::GetFileNameWithoutExtension($projectPath)
            }

            $category = if ($isTestProject) {
                'test'
            }
            elseif ($projectPath -like 'tests/FixtureWorkspace/*') {
                'fixture'
            }
            elseif ($projectPath -like 'tests/*') {
                'test-support'
            }
            elseif ($projectPath -like 'src/*') {
                'source'
            }
            else {
                'other'
            }

            [PSCustomObject]@{
                name = $name
                path = $projectPath
                category = $category
                isTestProject = $isTestProject
                includedInSolution = $solutionProjects.ContainsKey($projectPath)
            }
        }
    ) | Sort-Object path

    $projectIndex = [PSCustomObject]@{
        schemaVersion = 1
        generatedAtUtc = $generatedAtUtc
        solutionPath = 'RoslynKit.slnx'
        projects = $projectEntries
    }
    Write-JsonFile -Path (Join-Path $indexRoot 'project-index.json') -Data $projectIndex
    Write-Host "Wrote project-index.json ($($projectEntries.Count) projects)."

    $obsoleteRouteIndexPath = Join-Path $indexRoot 'route-index.json'
    if (Test-Path -LiteralPath $obsoleteRouteIndexPath) {
        Remove-Item -LiteralPath $obsoleteRouteIndexPath -Force
        Write-Host 'Removed stale route-index.json.'
    }

    $testIndex = [PSCustomObject]@{
        schemaVersion = 1
        generatedAtUtc = $generatedAtUtc
        testProjects = @(
            foreach ($project in $projectEntries | Where-Object { $_.category -in @('test', 'test-support') }) {
                [PSCustomObject]@{
                    name = $project.name
                    path = $project.path
                    category = $project.category
                }
            }
        )
        testFiles = @(
            foreach ($path in ($files | Where-Object { $_ -like 'tests/RoslynKit.Tests/*.cs' } | Sort-Object)) {
                $category = switch -Wildcard ($path) {
                    'tests/RoslynKit.Tests/CliParserTests.cs' { 'parser' ; break }
                    'tests/RoslynKit.Tests/CommandExecutionTests.cs' { 'execution' ; break }
                    'tests/RoslynKit.Tests/CliOutputTests.cs' { 'cli-output' ; break }
                    'tests/RoslynKit.Tests/MarkdownFormatTests.cs' { 'markdown-output' ; break }
                    'tests/RoslynKit.Tests/SymbolsCommandTests.cs' { 'symbols' ; break }
                    'tests/RoslynKit.Tests/TestPaths.cs' { 'support' ; break }
                    default { 'test' }
                }

                [PSCustomObject]@{
                    path = $path
                    category = $category
                }
            }
        )
        supportFiles = @(
            foreach ($path in ($files | Where-Object { $_ -like 'tests/RoslynKit.WorkspaceGraphDump/*' -or $_ -like 'tests/FixtureWorkspace/*' } | Sort-Object)) {
                $category = if ($path -like 'tests/RoslynKit.WorkspaceGraphDump/*') { 'workspace-utility' } else { 'fixture' }
                [PSCustomObject]@{
                    path = $path
                    category = $category
                }
            }
        )
    }
    Write-JsonFile -Path (Join-Path $indexRoot 'test-index.json') -Data $testIndex
    Write-Host "Wrote test-index.json ($($testIndex.testFiles.Count) main test files)."

    $roslynKit = Resolve-RoslynKitCommand
    if (-not $roslynKit) {
        Write-Host 'RoslynKit not available. Skipping symbol-index.json.'
    }
    else {
        $workspaceLines = & $roslynKit.Path workspace --target .\RoslynKit.slnx
        if ($LASTEXITCODE -ne 0) {
            throw "RoslynKit workspace failed. Symbol indexing skipped."
        }

        $parsedDocuments = @(
            foreach ($line in $workspaceLines) {
                if ($line -match '^- project: `(?<project>[^`]+)`(?: tfm: `(?<tfm>[^`]+)`)? kind: (?<kind>\S+) path: `(?<path>[^`]+)` key: `(?<key>[^`]+)`$') {
                    [PSCustomObject]@{
                        projectName = $Matches['project']
                        documentKind = $Matches['kind']
                        path = $Matches['path']
                    }
                }
            }
        )
        if ($parsedDocuments.Count -eq 0) {
            throw "RoslynKit workspace output contained no parsable document bullets. The resolved tool ($($roslynKit.Path)) may predate the markdown output contract."
        }

        $symbolDocuments = New-Object System.Collections.Generic.List[object]
        $skippedDocuments = New-Object System.Collections.Generic.List[object]
        $documents = @(
            $parsedDocuments |
                Where-Object { $_.documentKind -eq 'source' } |
                Where-Object {
                    $relative = Normalize-RepoPath -RepoRoot $repoRoot -Path $_.path
                    -not (Should-IgnorePath $relative)
                }
        )

        foreach ($document in $documents) {
            $relativePath = Normalize-RepoPath -RepoRoot $repoRoot -Path $document.path
            $commandPath = ".\$($relativePath.Replace('/', '\'))"

            try {
                $documentLines = & $roslynKit.Path document-symbols --target .\RoslynKit.slnx --file $commandPath
                if ($LASTEXITCODE -ne 0) {
                    throw "RoslynKit document-symbols exited with code $LASTEXITCODE."
                }

                $symbols = @(
                    foreach ($line in $documentLines) {
                        if ($line -match '^- kind: (?<kind>\S+) name: `(?<name>[^`]+)`(?: loc: `(?<loc>[^`]+)`)?(?: id: `(?<id>[^`]+)`)?$') {
                            $symbolKind = $Matches['kind']
                            $symbolName = $Matches['name']
                            $symbolLocation = $Matches['loc']
                            $symbolLine = 0
                            $symbolColumn = 0
                            if ($symbolLocation -and $symbolLocation -match ':(?<line>\d+):(?<column>\d+)-\d+:\d+$') {
                                $symbolLine = [int]$Matches['line']
                                $symbolColumn = [int]$Matches['column']
                            }

                            [PSCustomObject]@{
                                name = $symbolName
                                kind = $symbolKind
                                displayName = $symbolName
                                line = $symbolLine
                                column = $symbolColumn
                            }
                        }
                    }
                )

                $symbolDocuments.Add([PSCustomObject]@{
                    path = $relativePath
                    projectName = [string]$document.projectName
                    documentKind = [string]$document.documentKind
                    symbolCount = $symbols.Count
                    symbols = $symbols
                })
            }
            catch {
                $skippedDocuments.Add([PSCustomObject]@{
                    path = $relativePath
                    reason = $_.Exception.Message
                })
            }
        }

        $symbolIndex = [PSCustomObject]@{
            schemaVersion = 2
            generatedAtUtc = $generatedAtUtc
            targetPath = 'RoslynKit.slnx'
            toolName = $roslynKit.Name
            toolPath = $roslynKit.Path
            documents = $symbolDocuments.ToArray()
            skipped = $skippedDocuments.ToArray()
        }
        Write-JsonFile -Path (Join-Path $indexRoot 'symbol-index.json') -Data $symbolIndex
        Write-Host "Wrote symbol-index.json ($($symbolDocuments.Count) documents, $($skippedDocuments.Count) skipped)."
    }
}
finally {
    Pop-Location
}
