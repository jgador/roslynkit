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
. (Join-Path $repoRoot "scripts/benchmark-codex.ps1")

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

$defaultCodexHome = Get-DefaultCodexHome
$expectedCodexHome = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) ".codex"
Assert-True -Condition ([string]::Equals($defaultCodexHome, $expectedCodexHome, [System.StringComparison]::Ordinal)) -Message "The CODEX_HOME fallback did not use the current user profile."

$prompt = New-ConditionPrompt -Condition "roslynkit" -Prompt "Portability regression prompt."
Assert-True -Condition $prompt.Contains("--target ./RoslynKit.slnx") -Message "The RoslynKit target path is not portable."
Assert-True -Condition $prompt.Contains("--index-path ./artifacts/roslynkit.db") -Message "The RoslynKit index path is not portable."
Assert-True -Condition (Test-RoslynKitInvocation -Command "/opt/tools/roslynkit version" -ResolvedRoslynKitPath "roslynkit") -Message "An absolute native RoslynKit path was not recognized."
Assert-False -Condition (Test-RoslynKitInvocation -Command "/opt/tools/not-roslynkit version" -ResolvedRoslynKitPath "roslynkit") -Message "An unrelated absolute path was misclassified as RoslynKit."
if (-not $IsWindows) {
    Assert-False -Condition (Test-RoslynKitInvocation -Command "/opt/tools/RoslynKit version" -ResolvedRoslynKitPath "roslynkit") -Message "A differently cased Linux executable path was misclassified as RoslynKit."
}

Write-Host "PowerShell portability regression tests passed."
