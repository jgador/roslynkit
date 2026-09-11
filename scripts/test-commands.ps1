# Check every command listed by runtime help and report failures together.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CommandPath,
    [string]$ExpectedVersion,
    [string]$ValidationRoot,
    [ValidateRange(1, 1800)]
    [int]$CommandTimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "common/packaging.ps1")

function Find-TextPosition
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$Marker,
        [int]$ColumnOffset
    )

    # Derive one-based positions from fixture text so edits above a marker do not break navigation checks.
    $index = $Content.IndexOf($Marker, [System.StringComparison]::Ordinal)
    if ($index -lt 0)
    {
        throw "Fixture marker '$Marker' was not found."
    }

    $prefix = $Content.Substring(0, $index)
    $line = ([Regex]::Matches($prefix, "`n")).Count + 1
    $lastNewline = $prefix.LastIndexOf("`n", [System.StringComparison]::Ordinal)
    $column = $index - $lastNewline + $ColumnOffset

    return [pscustomobject]@{
        Line = $line
        Column = $column
    }
}

function Invoke-RoslynKitCase
{
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedText,
        [string]$ExpectedCommandVersion,
        [string[]]$ExpectedPaths = @()
    )

    $result = Invoke-CapturedProcess `
        -FilePath $resolvedCommandPath `
        -Arguments $Arguments `
        -WorkingDirectory $fixtureRoot `
        -TimeoutSeconds $CommandTimeoutSeconds
    # Collect failed expectations instead of throwing, allowing the remaining command cases to run.
    $reasons = [System.Collections.Generic.List[string]]::new()

    if ($result.TimedOut)
    {
        $reasons.Add("timed out after $CommandTimeoutSeconds seconds")
    }
    elseif ($result.ExitCode -ne 0)
    {
        $reasons.Add("exited with code $($result.ExitCode)")
    }

    foreach ($expected in $ExpectedText)
    {
        if (-not $result.StandardOutput.Contains($expected, [System.StringComparison]::Ordinal))
        {
            $reasons.Add("stdout did not contain '$expected'")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommandVersion) -and
        -not (Test-RoslynKitCommandVersionOutput `
            -VersionText $result.StandardOutput `
            -ExpectedVersion $ExpectedCommandVersion))
    {
        $reasons.Add("stdout did not report exact RoslynKit version '$ExpectedCommandVersion'")
    }

    foreach ($expectedPath in $ExpectedPaths)
    {
        if (-not (Test-Path -LiteralPath $expectedPath))
        {
            $reasons.Add("expected path was not created: $expectedPath")
        }
    }

    return [pscustomobject]@{
        Name = $Arguments[0]
        Invocation = Format-Invocation -FilePath $resolvedCommandPath -Arguments $Arguments
        ExitCode = $result.ExitCode
        TimedOut = $result.TimedOut
        StandardOutput = $result.StandardOutput
        StandardError = $result.StandardError
        Reasons = @($reasons)
        Passed = $reasons.Count -eq 0
    }
}

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath
if ([string]::IsNullOrWhiteSpace($ExpectedVersion))
{
    $ExpectedVersion = $context.PackageVersion
}

$resolvedCommandPath = Resolve-FullPath $CommandPath
if (-not (Test-Path -LiteralPath $resolvedCommandPath -PathType Leaf))
{
    throw "RoslynKit command was not found: $resolvedCommandPath"
}

$resolvedValidationRoot = if ([string]::IsNullOrWhiteSpace($ValidationRoot))
{
    Join-Path $context.RepoRoot "artifacts/command-validation/roslynkit"
}
else
{
    Resolve-FullPath $ValidationRoot
}

Reset-Directory `
    -Path $resolvedValidationRoot `
    -RootPath $context.RepoRoot `
    -Label "RoslynKit command validation root"

$fixtureRoot = Join-Path $resolvedValidationRoot "init-repository"
$projectPath = Join-Path $context.RepoRoot "tests/FixtureWorkspace/App/App.csproj"
$sourcePath = Join-Path $context.RepoRoot "tests/FixtureWorkspace/App/Source.cs"
$indexPath = Join-Path $resolvedValidationRoot "roslynkit.db"
$initSkillPath = Join-Path $fixtureRoot ".agents/skills/roslynkit/SKILL.md"

# A real repository marker lets default repository discovery run without modifying this checkout.
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $fixtureRoot ".git") -Force | Out-Null

# Restore once so case failures describe command behavior instead of missing dependencies.
$restoreResult = Invoke-CapturedProcess `
    -FilePath $context.DotNet `
    -Arguments @("restore", $projectPath, "--nologo") `
    -WorkingDirectory $context.RepoRoot `
    -TimeoutSeconds $CommandTimeoutSeconds
Assert-ProcessSucceeded -Description "Fixture restore" -Result $restoreResult

$sourceContent = Get-Content -LiteralPath $sourcePath -Raw
$classPosition = Find-TextPosition -Content $sourceContent -Marker "public sealed partial class GeneratedMessageSource"
$typePosition = Find-TextPosition -Content $sourceContent -Marker "source = _source"
$quickInfoPosition = Find-TextPosition `
    -Content $sourceContent `
    -Marker "source.GetMessage" `
    -ColumnOffset "source.".Length
$signaturePosition = Find-TextPosition `
    -Content $sourceContent `
    -Marker "return source.GetMessage(" `
    -ColumnOffset "return source.GetMessage(".Length

# Case names come from Arguments[0], keeping this list comparable with runtime help.
$commandCases = @(
    @{
        Arguments = @("serve", "--help")
        ExpectedText = @("command: serve", "--max-workspaces")
    }
    @{
        Arguments = @("version")
        ExpectedText = @()
        ExpectedCommandVersion = $ExpectedVersion
    }
    @{
        Arguments = @("init", "--agent", "codex")
        ExpectedText = @("command: init")
        ExpectedPaths = @($initSkillPath)
    }
    @{
        Arguments = @("workspace", "--target", $projectPath)
        ExpectedText = @("command: workspace", 'project: `App`')
    }
    @{
        Arguments = @("diagnostics", "--target", $projectPath, "--max-results", "20")
        ExpectedText = @("command: diagnostics")
    }
    @{
        Arguments = @("index", "--target", $projectPath, "--index-path", $indexPath, "--rebuild")
        ExpectedText = @("command: index", "index-state: fresh", "rebuilt: true")
        ExpectedPaths = @($indexPath)
    }
    @{
        Arguments = @(
            "search",
            "--target", $projectPath,
            "--index-path", $indexPath,
            "--query", "configuration validation performed",
            "--max-results", "50"
        )
        ExpectedText = @("command: search", "FixtureApp.ConfigurationValidator.ValidateConfiguration")
    }
    @{
        Arguments = @(
            "symbols",
            "--target", $projectPath,
            "--query", "GeneratedMessageSource",
            "--exact",
            "--kind", "class"
        )
        ExpectedText = @("command: symbols", "FixtureApp.GeneratedMessageSource")
    }
    @{
        Arguments = @("document-text", "--target", $projectPath, "--file", $sourcePath)
        ExpectedText = @("command: document-text", "public sealed partial class GeneratedMessageSource")
    }
    @{
        Arguments = @(
            "document-lines",
            "--target", $projectPath,
            "--file", $sourcePath,
            "--start-line", $classPosition.Line.ToString(),
            "--end-line", ($classPosition.Line + 14).ToString()
        )
        ExpectedText = @("command: document-lines", "public sealed partial class GeneratedMessageSource")
    }
    @{
        Arguments = @("document-symbols", "--target", $projectPath, "--file", $sourcePath)
        ExpectedText = @("command: document-symbols", "T:FixtureApp.GeneratedMessageSource")
    }
    @{
        Arguments = @(
            "definition",
            "--target", $projectPath,
            "--symbol", "T:FixtureApp.GeneratedMessageSource"
        )
        ExpectedText = @("command: definition", "FixtureApp.GeneratedMessageSource")
    }
    @{
        Arguments = @(
            "type-definition",
            "--target", $projectPath,
            "--file", $sourcePath,
            "--line", $typePosition.Line.ToString(),
            "--column", $typePosition.Column.ToString()
        )
        ExpectedText = @("command: type-definition", "FixtureApp.IMessageSource")
    }
    @{
        Arguments = @(
            "references",
            "--target", $projectPath,
            "--symbol", "M:FixtureApp.IMessageSource.GetMessage(System.String)",
            "--max-results", "10"
        )
        ExpectedText = @("command: references", "Source.cs")
    }
    @{
        Arguments = @(
            "implementations",
            "--target", $projectPath,
            "--symbol", "T:FixtureApp.IMessageSource",
            "--max-results", "10"
        )
        ExpectedText = @("command: implementations", "FixtureApp.GeneratedMessageSource")
    }
    @{
        Arguments = @(
            "symbol-context",
            "--target", $projectPath,
            "--symbol", "M:FixtureApp.Consumer.Run"
        )
        ExpectedText = @("command: symbol-context", "FixtureApp.Consumer.Run")
    }
    @{
        Arguments = @(
            "quick-info",
            "--target", $projectPath,
            "--file", $sourcePath,
            "--line", $quickInfoPosition.Line.ToString(),
            "--column", $quickInfoPosition.Column.ToString()
        )
        ExpectedText = @("command: quick-info", "GetMessage")
    }
    @{
        Arguments = @(
            "signature-help",
            "--target", $projectPath,
            "--file", $sourcePath,
            "--line", $signaturePosition.Line.ToString(),
            "--column", $signaturePosition.Column.ToString()
        )
        ExpectedText = @("command: signature-help", "- signature:", "GetMessage")
    }
    @{
        Arguments = @(
            "symbol-source",
            "--target", $projectPath,
            "--symbol", "M:FixtureApp.Consumer.Run"
        )
        ExpectedText = @("command: symbol-source", "public string Run()")
    }
)

$results = [System.Collections.Generic.List[object]]::new()
$helpResult = Invoke-RoslynKitCase `
    -Arguments @("help") `
    -ExpectedText @("tool: roslynkit", "- command:")
$results.Add($helpResult)

$discoveredCommands = @(
    [Regex]::Matches($helpResult.StandardOutput, '(?m)^- command: `([^`]+)`') |
        ForEach-Object { $_.Groups[1].Value }
)
# Runtime help is the command inventory; this detects missing and stale smoke cases.
$caseNames = @($commandCases | ForEach-Object { $_.Arguments[0] })
$missingCases = @($discoveredCommands | Where-Object { $_ -notin $caseNames })
$staleCases = @($caseNames | Where-Object { $_ -notin $discoveredCommands })

$coverageReasons = [System.Collections.Generic.List[string]]::new()
if ($missingCases.Count -gt 0)
{
    $coverageReasons.Add("commands missing smoke cases: $($missingCases -join ', ')")
}

if ($staleCases.Count -gt 0)
{
    $coverageReasons.Add("smoke cases not present in runtime help: $($staleCases -join ', ')")
}

$results.Add([pscustomobject]@{
    Name = "command-coverage"
    Invocation = "compare runtime help with exhaustive smoke cases"
    ExitCode = 0
    TimedOut = $false
    StandardOutput = $helpResult.StandardOutput
    StandardError = $helpResult.StandardError
    Reasons = @($coverageReasons)
    Passed = $coverageReasons.Count -eq 0
})

Write-Host "Testing $($discoveredCommands.Count) RoslynKit commands..."
foreach ($commandCase in $commandCases)
{
    Write-Verbose "Testing roslynkit $($commandCase.Arguments[0])..."
    $caseResult = Invoke-RoslynKitCase @commandCase
    $results.Add($caseResult)
}

$commandResults = @($results | Where-Object { $_.Name -in $caseNames })
$failures = @($results | Where-Object { -not $_.Passed })
$passedCommands = @($commandResults | Where-Object { $_.Passed }).Count

Write-Host ""
Write-Verbose "Command: $resolvedCommandPath"
Write-Verbose "Expected version: $ExpectedVersion"
Write-Host "RoslynKit commands passed: $passedCommands/$($discoveredCommands.Count); failed checks: $($failures.Count)"

if ($failures.Count -gt 0)
{
    # Emit every captured failure before throwing so one run provides the full diagnosis.
    Write-Host ""
    Write-Host "Failures:"
    foreach ($failure in $failures)
    {
        Write-Host ""
        Write-Host "[$($failure.Name)] $($failure.Invocation)"
        Write-Host "Reason: $($failure.Reasons -join '; ')"
        Write-Host "Exit code: $($failure.ExitCode)"
        if (-not [string]::IsNullOrWhiteSpace($failure.StandardOutput))
        {
            Write-Host "stdout:"
            Write-Host $failure.StandardOutput.TrimEnd()
        }

        if (-not [string]::IsNullOrWhiteSpace($failure.StandardError))
        {
            Write-Host "stderr:"
            Write-Host $failure.StandardError.TrimEnd()
        }
    }

    throw "RoslynKit exhaustive command smoke test failed with $($failures.Count) error(s)."
}
