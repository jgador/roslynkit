[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-False {
    param([bool] $Condition, [string] $Message)
    Assert-True -Condition (-not $Condition) -Message $Message
}

function Assert-Throws {
    param([scriptblock] $Action, [string] $Message)
    try {
        & $Action
    }
    catch {
        return
    }
    throw $Message
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $repoRoot "scripts/RoslynKit.Packaging.ps1")

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "roslynkit-portability-regression"
$childPath = Join-Path $testRoot "packages"
$siblingPath = "$testRoot-sibling"

Assert-PathUnderRoot -Path $childPath -RootPath $testRoot -Label "Child path"
Assert-Throws -Action { Assert-PathUnderRoot -Path $testRoot -RootPath $testRoot -Label "Root path" } -Message "The protected root path was accepted."
Assert-Throws -Action { Assert-PathUnderRoot -Path "$testRoot/" -RootPath $testRoot -Label "Root path with a trailing separator" } -Message "The protected root path with a trailing separator was accepted."
Assert-Throws -Action { Assert-PathUnderRoot -Path $siblingPath -RootPath $testRoot -Label "Sibling path" } -Message "A sibling with the root prefix was accepted."

$caseSensitiveRoot = Join-Path ([System.IO.Path]::GetTempPath()) "roslynkit-case-sensitive-root"
$caseVariantChild = Join-Path ([System.IO.Path]::GetTempPath()) "ROSLYNKIT-CASE-SENSITIVE-ROOT/child"
if ($IsWindows) {
    Assert-PathUnderRoot -Path $caseVariantChild -RootPath $caseSensitiveRoot -Label "Windows case-insensitive child"
}
else {
    Assert-Throws -Action { Assert-PathUnderRoot -Path $caseVariantChild -RootPath $caseSensitiveRoot -Label "Case-variant child" } -Message "A differently cased path was accepted on a case-sensitive platform."
}

$reparseRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("roslynkit-reparse-root-" + [Guid]::NewGuid())
$outsideRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("roslynkit-reparse-outside-" + [Guid]::NewGuid())
$outsideSentinel = Join-Path $outsideRoot "sentinel.txt"
$reparsePath = Join-Path $reparseRoot "package-link"
$missingFeedPath = Join-Path $reparsePath "not-yet-created"
try {
    New-Item -ItemType Directory -Path $reparseRoot | Out-Null
    New-Item -ItemType Directory -Path $outsideRoot | Out-Null
    Set-Content -LiteralPath $outsideSentinel -Value "outside sentinel" -NoNewline
    try {
        New-Item -ItemType SymbolicLink -Path $reparsePath -Target $outsideRoot | Out-Null
    }
    catch {
        if (-not $IsWindows) {
            throw
        }

        New-Item -ItemType Junction -Path $reparsePath -Target $outsideRoot | Out-Null
    }

    Assert-False -Condition (Test-Path -LiteralPath $missingFeedPath) -Message "The reparse-point feed target unexpectedly existed before the reset."
    Assert-Throws -Action { Reset-Directory -Path $missingFeedPath -RootPath $reparseRoot -Label "Reparse-point feed" } -Message "A package-feed path through an external reparse point was accepted."
    Assert-True -Condition (Test-Path -LiteralPath $outsideSentinel -PathType Leaf) -Message "The external sentinel was removed through the reparse point."
    Assert-False -Condition (Test-Path -LiteralPath $missingFeedPath) -Message "The missing feed path was created through the reparse point."
}
finally {
    if (Test-Path -LiteralPath $reparsePath) {
        [System.IO.Directory]::Delete($reparsePath)
    }

    if (Test-Path -LiteralPath $reparseRoot) {
        [System.IO.Directory]::Delete($reparseRoot)
    }

    if (Test-Path -LiteralPath $outsideRoot) {
        Remove-Item -LiteralPath $outsideRoot -Recurse -Force
    }
}

$agentsInstructions = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "AGENTS.md")
Assert-True -Condition $agentsInstructions.Contains("pwsh -Command 'Get-Content AGENTS.md'") -Message "The agent instructions did not provide an explicit pwsh wrapper for PowerShell cmdlets under WSL or Linux."

$securityAuditSkill = Get-Content -Raw -LiteralPath (Join-Path $repoRoot ".agents/skills/security-audit/SKILL.md")
$securityAuditScannerProbe = "pwsh -NoProfile -Command 'Get-Command gitleaks,trufflehog -ErrorAction SilentlyContinue'"
Assert-True -Condition $securityAuditSkill.Contains($securityAuditScannerProbe) -Message "The security-audit scanner probe did not use an explicit PowerShell host."
Assert-True -Condition $securityAuditSkill.Contains("do not submit individual PowerShell cmdlets to Bash") -Message "The security-audit multiline commands did not require an explicit PowerShell execution context."

$roslynKitDevSkill = Get-Content -Raw -LiteralPath (Join-Path $repoRoot ".agents/skills/roslynkit-dev/SKILL.md")
Assert-True -Condition $roslynKitDevSkill.Contains("--max-results 10") -Message "The development skill did not begin search discovery with 10 results."
Assert-True -Condition $roslynKitDevSkill.Contains('one third and final search with `--max-results 20`') -Message "The development skill did not cap ordinary fallback expansion at 20 results."
Assert-True -Condition $roslynKitDevSkill.Contains('`--max-results 50` only when the first two rankings show many plausible near-ties') -Message "The development skill did not reserve 50 results for demonstrated ranking ambiguity."

$claudeWrapperPaths = @(
    ".claude/skills/commit-context/SKILL.md",
    ".claude/skills/git-commit-push/SKILL.md",
    ".claude/skills/roslynkit-dev/SKILL.md"
)
foreach ($claudeWrapperPath in $claudeWrapperPaths) {
    $claudeWrapper = Get-Content -Raw -LiteralPath (Join-Path $repoRoot $claudeWrapperPath)
    Assert-True -Condition $claudeWrapper.Contains('!`pwsh -NoProfile -Command "Get-Content') -Message "$claudeWrapperPath did not use the cross-platform pwsh host."
    Assert-False -Condition $claudeWrapper.Contains('!`powershell.exe -NoProfile -Command "Get-Content') -Message "$claudeWrapperPath retained the Windows-only PowerShell host."
}

Write-Host "PowerShell portability regression tests passed."
