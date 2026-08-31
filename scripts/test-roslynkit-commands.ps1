[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CommandPath,
    [string]$ExpectedVersion,
    [string]$ValidationRoot,
    [ValidateRange(1, 1800)]
    [int]$CommandTimeoutSeconds = 180,
    [switch]$PrintManualCommands
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RoslynKit.Packaging.ps1")

function Invoke-CapturedProcess
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments)
    {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try
    {
        if (-not $process.Start())
        {
            throw "Unable to start process '$FilePath'."
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut)
        {
            if (-not $process.HasExited)
            {
                $process.Kill($true)
            }

            $process.WaitForExit()
        }

        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        $exitCode = if ($timedOut) { -1 } else { $process.ExitCode }

        return [pscustomobject]@{
            ExitCode = $exitCode
            TimedOut = $timedOut
            StandardOutput = $standardOutput
            StandardError = $standardError
        }
    }
    finally
    {
        $process.Dispose()
    }
}

function Assert-SetupCommandSucceeded
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Result
    )

    if (-not $Result.TimedOut -and $Result.ExitCode -eq 0)
    {
        return
    }

    $reason = if ($Result.TimedOut)
    {
        "timed out"
    }
    else
    {
        "failed with exit code $($Result.ExitCode)"
    }

    throw "$Description $reason.`nstdout:`n$($Result.StandardOutput)`nstderr:`n$($Result.StandardError)"
}

function Find-TextPosition
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$Marker,
        [int]$ColumnOffset
    )

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

function Format-Invocation
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $quotedFilePath = "'$($FilePath.Replace("'", "''"))'"
    $quotedArguments = $Arguments | ForEach-Object { "'$($_.Replace("'", "''"))'" }
    return "& $quotedFilePath $($quotedArguments -join ' ')".TrimEnd()
}

function Format-ManualInvocation
{
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $formattedArguments = $Arguments | ForEach-Object {
        if ($_ -match "^[A-Za-z0-9_./:\\-]+$")
        {
            $_
        }
        else
        {
            "'$($_.Replace("'", "''"))'"
        }
    }

    return "roslynkit $($formattedArguments -join ' ')".TrimEnd()
}

function Write-ManualTestCase
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedText,
        [string]$ExpectedCommandVersion,
        [string[]]$ExpectedPaths = @()
    )

    Write-Host "    # $Name"
    Write-Host "    # Expect exit code: 0"
    foreach ($expected in $ExpectedText)
    {
        Write-Host "    # Expect stdout containing: $expected"
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommandVersion))
    {
        Write-Host "    # Expect RoslynKit package version: $ExpectedCommandVersion (an optional +build metadata suffix is valid)"
    }

    foreach ($expectedPath in $ExpectedPaths)
    {
        Write-Host "    # Expect created path: $expectedPath"
    }

    Write-Host "    $(Format-ManualInvocation -Arguments $Arguments)"
    Write-Host ""
}

function Invoke-RoslynKitCase
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
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
        Name = $Name
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

New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $fixtureRoot ".git") -Force | Out-Null

$restoreResult = Invoke-CapturedProcess `
    -FilePath $context.DotNet `
    -Arguments @("restore", $projectPath, "--nologo") `
    -WorkingDirectory $context.RepoRoot `
    -TimeoutSeconds $CommandTimeoutSeconds
Assert-SetupCommandSucceeded -Description "Fixture restore" -Result $restoreResult

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

$commandCases = @(
    [pscustomobject]@{
        Name = "version"
        Arguments = @("version")
        ExpectedText = @()
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "init"
        Arguments = @("init", "--agent", "codex")
        ExpectedText = @("command: init")
        ExpectedPaths = @($initSkillPath)
    }
    [pscustomobject]@{
        Name = "workspace"
        Arguments = @("workspace", "--target", $projectPath)
        ExpectedText = @("command: workspace", 'project: `App`')
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "diagnostics"
        Arguments = @("diagnostics", "--target", $projectPath, "--max-results", "20")
        ExpectedText = @("command: diagnostics")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "index"
        Arguments = @("index", "--target", $projectPath, "--index-path", $indexPath, "--rebuild")
        ExpectedText = @("command: index", "index-state: fresh", "rebuilt: true")
        ExpectedPaths = @($indexPath)
    }
    [pscustomobject]@{
        Name = "search"
        Arguments = @(
            "search",
            "--target", $projectPath,
            "--index-path", $indexPath,
            "--query", "configuration validation performed",
            "--max-results", "50"
        )
        ExpectedText = @("command: search", "FixtureApp.ConfigurationValidator.ValidateConfiguration")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "symbols"
        Arguments = @(
            "symbols",
            "--target", $projectPath,
            "--query", "GeneratedMessageSource",
            "--exact",
            "--kind", "class"
        )
        ExpectedText = @("command: symbols", "FixtureApp.GeneratedMessageSource")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "document-text"
        Arguments = @("document-text", "--target", $projectPath, "--file", $sourcePath)
        ExpectedText = @("command: document-text", "public sealed partial class GeneratedMessageSource")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "document-lines"
        Arguments = @(
            "document-lines",
            "--target", $projectPath,
            "--file", $sourcePath,
            "--start-line", $classPosition.Line.ToString(),
            "--end-line", ($classPosition.Line + 14).ToString()
        )
        ExpectedText = @("command: document-lines", "public sealed partial class GeneratedMessageSource")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "document-symbols"
        Arguments = @("document-symbols", "--target", $projectPath, "--file", $sourcePath)
        ExpectedText = @("command: document-symbols", "T:FixtureApp.GeneratedMessageSource")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "definition"
        Arguments = @(
            "definition",
            "--target", $projectPath,
            "--symbol", "T:FixtureApp.GeneratedMessageSource"
        )
        ExpectedText = @("command: definition", "FixtureApp.GeneratedMessageSource")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "type-definition"
        Arguments = @(
            "type-definition",
            "--target", $projectPath,
            "--file", $sourcePath,
            "--line", $typePosition.Line.ToString(),
            "--column", $typePosition.Column.ToString()
        )
        ExpectedText = @("command: type-definition", "FixtureApp.IMessageSource")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "references"
        Arguments = @(
            "references",
            "--target", $projectPath,
            "--symbol", "M:FixtureApp.IMessageSource.GetMessage(System.String)",
            "--max-results", "10"
        )
        ExpectedText = @("command: references", "Source.cs")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "implementations"
        Arguments = @(
            "implementations",
            "--target", $projectPath,
            "--symbol", "T:FixtureApp.IMessageSource",
            "--max-results", "10"
        )
        ExpectedText = @("command: implementations", "FixtureApp.GeneratedMessageSource")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "symbol-context"
        Arguments = @(
            "symbol-context",
            "--target", $projectPath,
            "--symbol", "M:FixtureApp.Consumer.Run"
        )
        ExpectedText = @("command: symbol-context", "FixtureApp.Consumer.Run")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "quick-info"
        Arguments = @(
            "quick-info",
            "--target", $projectPath,
            "--file", $sourcePath,
            "--line", $quickInfoPosition.Line.ToString(),
            "--column", $quickInfoPosition.Column.ToString()
        )
        ExpectedText = @("command: quick-info", "GetMessage")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "signature-help"
        Arguments = @(
            "signature-help",
            "--target", $projectPath,
            "--file", $sourcePath,
            "--line", $signaturePosition.Line.ToString(),
            "--column", $signaturePosition.Column.ToString()
        )
        ExpectedText = @("command: signature-help", "- signature:", "GetMessage")
        ExpectedPaths = @()
    }
    [pscustomobject]@{
        Name = "symbol-source"
        Arguments = @(
            "symbol-source",
            "--target", $projectPath,
            "--symbol", "M:FixtureApp.Consumer.Run"
        )
        ExpectedText = @("command: symbol-source", "public string Run()")
        ExpectedPaths = @()
    }
)

$results = [System.Collections.Generic.List[object]]::new()
$helpResult = Invoke-RoslynKitCase `
    -Name "help" `
    -Arguments @("help") `
    -ExpectedText @("tool: roslynkit", "- command:") `
    -ExpectedPaths @()
$results.Add($helpResult)

$discoveredCommands = @(
    [Regex]::Matches($helpResult.StandardOutput, '(?m)^- command: `([^`]+)`') |
        ForEach-Object { $_.Groups[1].Value }
)
$caseNames = @($commandCases | ForEach-Object { $_.Name })
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

if ($PrintManualCommands)
{
    if (-not $helpResult.Passed)
    {
        throw "Unable to prepare the manual command checklist because 'roslynkit help' failed validation: $($helpResult.Reasons -join '; ')`nstdout:`n$($helpResult.StandardOutput)`nstderr:`n$($helpResult.StandardError)"
    }

    if ($coverageReasons.Count -gt 0)
    {
        throw "Unable to prepare the manual command checklist because exhaustive command coverage is stale: $($coverageReasons -join '; ')"
    }

    $manualCommandCount = $commandCases.Count + 1
    $quotedFixtureRoot = "'$($fixtureRoot.Replace("'", "''"))'"

    Write-Host ""
    Write-Host "RoslynKit manual exhaustive command checklist"
    Write-Host "Command: $resolvedCommandPath"
    Write-Host "Expected version: $ExpectedVersion"
    Write-Host "Commands discovered: $($discoveredCommands.Count)"
    Write-Host "Manual commands printed: $manualCommandCount"
    Write-Host "Validation workspace: $fixtureRoot"
    Write-Host ""
    Write-Host "Copy and run this PowerShell block in order:"
    Write-Host ""
    Write-Host "Push-Location -LiteralPath $quotedFixtureRoot"
    Write-Host "try"
    Write-Host "{"
    Write-ManualTestCase `
        -Name "help" `
        -Arguments @("help") `
        -ExpectedText @("tool: roslynkit", "- command:")

    foreach ($commandCase in $commandCases)
    {
        $caseExpectedVersion = if ($commandCase.Name -eq "version") { $ExpectedVersion } else { $null }
        Write-ManualTestCase `
            -Name $commandCase.Name `
            -Arguments $commandCase.Arguments `
            -ExpectedText $commandCase.ExpectedText `
            -ExpectedCommandVersion $caseExpectedVersion `
            -ExpectedPaths $commandCase.ExpectedPaths
    }

    Write-Host "}"
    Write-Host "finally"
    Write-Host "{"
    Write-Host "    Pop-Location"
    Write-Host "}"
    Write-Host ""
    Write-Host "No representative RoslynKit command was executed; run the printed block to perform the manual test."
    return
}

foreach ($commandCase in $commandCases)
{
    Write-Host "Testing roslynkit $($commandCase.Name)..."
    $caseExpectedVersion = if ($commandCase.Name -eq "version") { $ExpectedVersion } else { $null }
    $caseResult = Invoke-RoslynKitCase `
        -Name $commandCase.Name `
        -Arguments $commandCase.Arguments `
        -ExpectedText $commandCase.ExpectedText `
        -ExpectedCommandVersion $caseExpectedVersion `
        -ExpectedPaths $commandCase.ExpectedPaths
    $results.Add($caseResult)
}

$commandResults = @($results | Where-Object { $_.Name -in $caseNames })
$failures = @($results | Where-Object { -not $_.Passed })
$passedCommands = @($commandResults | Where-Object { $_.Passed }).Count

Write-Host ""
Write-Host "RoslynKit exhaustive command smoke-test summary"
Write-Host "Command: $resolvedCommandPath"
Write-Host "Expected version: $ExpectedVersion"
Write-Host "Commands discovered: $($discoveredCommands.Count)"
Write-Host "Commands exercised: $($commandResults.Count)"
Write-Host "Commands passed: $passedCommands"
Write-Host "Failed checks: $($failures.Count)"

if ($failures.Count -gt 0)
{
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

Write-Host ""
Write-Host "RoslynKit exhaustive command smoke test passed."
