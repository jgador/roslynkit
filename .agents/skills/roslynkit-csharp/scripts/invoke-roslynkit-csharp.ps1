[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'workspace',
        'declaration-lookup',
        'file-structure',
        'body-read',
        'definition',
        'references',
        'implementations',
        'quick-info',
        'type-definition',
        'signature-help',
        'generated-document-read'
    )]
    [string]$Operation,

    [string]$Target,
    [string]$SearchRoot,
    [string]$Path,
    [string]$DocumentKey,
    [string]$Query,
    [string]$Kind,
    [int]$Line,
    [int]$Column,
    [int]$StartLine,
    [int]$StartColumn,
    [int]$EndLine,
    [int]$EndColumn,
    [int]$MaxResults,
    [switch]$Exact,
    [switch]$CaseSensitive,
    [switch]$IncludeGenerated,
    [switch]$IncludeAdditional,
    [switch]$IncludeAnalyzerConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-RepositoryRoot {
    $scriptDirectory = $PSScriptRoot
    $current = Get-Item -LiteralPath (Join-Path $scriptDirectory '..\..\..\..')

    while ($null -ne $current) {
        if (Test-Path -LiteralPath (Join-Path $current.FullName '.git')) {
            return $current.FullName
        }

        $current = $current.Parent
    }

    throw "Could not resolve the RoslynKit repository root from '$scriptDirectory'."
}

function Resolve-FullPath([string]$PathValue) {
    return [System.IO.Path]::GetFullPath($PathValue)
}

function Resolve-SearchDirectory {
    if (-not [string]::IsNullOrWhiteSpace($SearchRoot)) {
        return Resolve-FullPath $SearchRoot
    }

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        $resolvedFile = Resolve-FullPath $Path
        return Split-Path -Parent $resolvedFile
    }

    return (Get-Location).Path
}

function Resolve-NearestTarget([string]$StartDirectory) {
    $current = Resolve-FullPath $StartDirectory

    while ($true) {
        foreach ($pattern in @('*.slnx', '*.sln', '*.csproj')) {
            $candidate = Get-ChildItem -LiteralPath $current -File -Filter $pattern | Sort-Object Name | Select-Object -First 1
            if ($null -ne $candidate) {
                return $candidate.FullName
            }
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) {
            break
        }

        $current = $parent
    }

    throw "Could not resolve a nearby .slnx, .sln, or .csproj target from '$StartDirectory'."
}

function Resolve-RoslynKitInvocation {
    $repoRoot = Resolve-RepositoryRoot
    $localProject = Join-Path $repoRoot 'src\RoslynKit\RoslynKit.csproj'
    $toolCommand = Get-Command roslynkit -ErrorAction SilentlyContinue

    if (Test-Path -LiteralPath $localProject) {
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
        return @{
            Type = 'dotnet-run'
            Command = $dotnet
            Project = $localProject
            Workdir = $repoRoot
        }
    }

    if ($null -ne $toolCommand) {
        return @{
            Type = 'tool'
            Command = $toolCommand.Source
            Workdir = (Get-Location).Path
        }
    }

    throw "Could not find an installed 'roslynkit' command or a local RoslynKit project at '$localProject'."
}

function Add-SelectorArguments([System.Collections.Generic.List[string]]$Arguments) {
    $hasFile = -not [string]::IsNullOrWhiteSpace($Path)
    $hasDocumentKey = -not [string]::IsNullOrWhiteSpace($DocumentKey)

    if ($hasFile -eq $hasDocumentKey) {
        throw "Exactly one of -Path or -DocumentKey is required for '$Operation'."
    }

    if ($hasFile) {
        $Arguments.Add('--file')
        $Arguments.Add((Resolve-FullPath $Path))
    }
    else {
        $Arguments.Add('--document-key')
        $Arguments.Add($DocumentKey)
    }
}

function Add-LineAndColumnArguments([System.Collections.Generic.List[string]]$Arguments) {
    if ($Line -le 0 -or $Column -le 0) {
        throw "Both -Line and -Column are required for '$Operation'."
    }

    $Arguments.Add('--line')
    $Arguments.Add($Line.ToString())
    $Arguments.Add('--column')
    $Arguments.Add($Column.ToString())
}

$resolvedTarget = if ($PSBoundParameters.ContainsKey('Target') -and -not [string]::IsNullOrWhiteSpace($Target)) {
    Resolve-FullPath $Target
}
else {
    Resolve-NearestTarget (Resolve-SearchDirectory)
}

$roslynArguments = [System.Collections.Generic.List[string]]::new()
switch ($Operation) {
    'workspace' {
        $roslynArguments.Add('workspace')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)

        if ($IncludeGenerated) {
            $roslynArguments.Add('--include-generated')
        }

        if ($IncludeAdditional) {
            $roslynArguments.Add('--include-additional')
        }

        if ($IncludeAnalyzerConfig) {
            $roslynArguments.Add('--include-analyzer-config')
        }
    }

    'declaration-lookup' {
        if (-not $PSBoundParameters.ContainsKey('Query') -or [string]::IsNullOrWhiteSpace($Query)) {
            throw "-Query is required for 'declaration-lookup'."
        }

        $roslynArguments.Add('symbols')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        $roslynArguments.Add('--query')
        $roslynArguments.Add($Query)

        if ($PSBoundParameters.ContainsKey('Kind') -and -not [string]::IsNullOrWhiteSpace($Kind)) {
            $roslynArguments.Add('--kind')
            $roslynArguments.Add($Kind)
        }

        if ($PSBoundParameters.ContainsKey('MaxResults') -and $MaxResults -gt 0) {
            $roslynArguments.Add('--max-results')
            $roslynArguments.Add($MaxResults.ToString())
        }

        if ($Exact) {
            $roslynArguments.Add('--exact')
        }

        if ($CaseSensitive) {
            $roslynArguments.Add('--case-sensitive')
        }
    }

    'file-structure' {
        $roslynArguments.Add('document-symbols')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        Add-SelectorArguments $roslynArguments
    }

    'body-read' {
        $roslynArguments.Add('document-text')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        Add-SelectorArguments $roslynArguments

        if ($PSBoundParameters.ContainsKey('StartLine') -and $StartLine -gt 0) {
            $roslynArguments.Add('--start-line')
            $roslynArguments.Add($StartLine.ToString())
        }

        if ($PSBoundParameters.ContainsKey('StartColumn') -and $StartColumn -gt 0) {
            $roslynArguments.Add('--start-column')
            $roslynArguments.Add($StartColumn.ToString())
        }

        if ($PSBoundParameters.ContainsKey('EndLine') -and $EndLine -gt 0) {
            $roslynArguments.Add('--end-line')
            $roslynArguments.Add($EndLine.ToString())
        }

        if ($PSBoundParameters.ContainsKey('EndColumn') -and $EndColumn -gt 0) {
            $roslynArguments.Add('--end-column')
            $roslynArguments.Add($EndColumn.ToString())
        }
    }

    'definition' {
        $roslynArguments.Add('definition')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        Add-SelectorArguments $roslynArguments
        Add-LineAndColumnArguments $roslynArguments
    }

    'references' {
        $roslynArguments.Add('references')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        Add-SelectorArguments $roslynArguments
        Add-LineAndColumnArguments $roslynArguments

        if ($PSBoundParameters.ContainsKey('MaxResults') -and $MaxResults -gt 0) {
            $roslynArguments.Add('--max-results')
            $roslynArguments.Add($MaxResults.ToString())
        }
    }

    'implementations' {
        $roslynArguments.Add('implementations')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        Add-SelectorArguments $roslynArguments
        Add-LineAndColumnArguments $roslynArguments

        if ($PSBoundParameters.ContainsKey('MaxResults') -and $MaxResults -gt 0) {
            $roslynArguments.Add('--max-results')
            $roslynArguments.Add($MaxResults.ToString())
        }
    }

    'quick-info' {
        $roslynArguments.Add('quick-info')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        Add-SelectorArguments $roslynArguments
        Add-LineAndColumnArguments $roslynArguments
    }

    'type-definition' {
        $roslynArguments.Add('type-definition')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        Add-SelectorArguments $roslynArguments
        Add-LineAndColumnArguments $roslynArguments
    }

    'signature-help' {
        $roslynArguments.Add('signature-help')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        Add-SelectorArguments $roslynArguments
        Add-LineAndColumnArguments $roslynArguments
    }

    'generated-document-read' {
        $roslynArguments.Add('document-text')
        $roslynArguments.Add('--target')
        $roslynArguments.Add($resolvedTarget)
        Add-SelectorArguments $roslynArguments
    }
}

$invocation = Resolve-RoslynKitInvocation
Push-Location $invocation.Workdir
try {
    switch ($invocation.Type) {
        'tool' {
            & $invocation.Command @roslynArguments
        }

        'dotnet-run' {
            & $invocation.Command run --project $invocation.Project --no-launch-profile --verbosity quiet -- @roslynArguments
        }

        default {
            throw "Unsupported RoslynKit invocation type '$($invocation.Type)'."
        }
    }

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
