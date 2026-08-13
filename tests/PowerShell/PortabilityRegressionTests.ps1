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

function New-BenchmarkCommandEvent {
    param(
        [string] $Type,
        [string] $Id,
        [string] $Command,
        [string] $Status = "completed",
        [object] $ExitCode = 0
    )
    return [pscustomobject]@{
        type = $Type
        item = [pscustomobject]@{
            id = $Id
            type = "command_execution"
            name = $null
            command = $Command
            status = $Status
            exit_code = $ExitCode
        }
        payload = $null
    }
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$benchmarkScriptPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../scripts/benchmark-codex.ps1"))
$originalCodexThreadId = [Environment]::GetEnvironmentVariable("CODEX_THREAD_ID", [EnvironmentVariableTarget]::Process)
$dotSourceThreadId = "roslynkit-portability-regression"
try {
    [Environment]::SetEnvironmentVariable("CODEX_THREAD_ID", $dotSourceThreadId, [EnvironmentVariableTarget]::Process)
    . (Join-Path $repoRoot "scripts/RoslynKit.Packaging.ps1")
    . (Join-Path $repoRoot "scripts/benchmark-codex.ps1")
    Assert-True -Condition ([string]::Equals([Environment]::GetEnvironmentVariable("CODEX_THREAD_ID", [EnvironmentVariableTarget]::Process), $dotSourceThreadId, [System.StringComparison]::Ordinal)) -Message "Dot-sourcing the benchmark script cleared CODEX_THREAD_ID."
}
finally {
    [Environment]::SetEnvironmentVariable("CODEX_THREAD_ID", $originalCodexThreadId, [EnvironmentVariableTarget]::Process)
}

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

$defaultCodexHome = Get-DefaultCodexHome
$expectedCodexHome = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) ".codex"
Assert-True -Condition ([string]::Equals($defaultCodexHome, $expectedCodexHome, [System.StringComparison]::Ordinal)) -Message "The CODEX_HOME fallback did not use the current user profile."
$hostKind = Get-BenchmarkHostKind
if ($IsWindows) {
    Assert-True -Condition ($hostKind -eq "windows") -Message "Native Windows was not classified as windows."
}
elseif (-not [string]::IsNullOrWhiteSpace($env:WSL_DISTRO_NAME) -and
    (-not [string]::IsNullOrWhiteSpace($env:VSCODE_IPC_HOOK_CLI) -or -not [string]::IsNullOrWhiteSpace($env:VSCODE_GIT_IPC_HANDLE) -or $env:TERM_PROGRAM -eq "vscode")) {
    Assert-True -Condition ($hostKind -eq "wsl-vscode-remote") -Message "VS Code Remote-WSL was not classified correctly."
}
elseif (-not [string]::IsNullOrWhiteSpace($env:WSL_DISTRO_NAME)) {
    Assert-True -Condition ($hostKind -eq "wsl") -Message "Native WSL was not classified as wsl."
}

$agentsInstructions = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "AGENTS.md")
Assert-True -Condition $agentsInstructions.Contains("pwsh -Command 'Get-Content AGENTS.md'") -Message "The agent instructions did not provide an explicit pwsh wrapper for PowerShell cmdlets under WSL or Linux."

$securityAuditSkill = Get-Content -Raw -LiteralPath (Join-Path $repoRoot ".agents/skills/security-audit/SKILL.md")
$securityAuditScannerProbe = "pwsh -NoProfile -Command 'Get-Command gitleaks,trufflehog -ErrorAction SilentlyContinue'"
Assert-True -Condition $securityAuditSkill.Contains($securityAuditScannerProbe) -Message "The security-audit scanner probe did not use an explicit PowerShell host."
Assert-True -Condition $securityAuditSkill.Contains("do not submit individual PowerShell cmdlets to Bash") -Message "The security-audit multiline commands did not require an explicit PowerShell execution context."

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

$prompt = New-ConditionPrompt -Condition "roslynkit" -Prompt "Portability regression prompt."
$benchmarkSkillReadCommand = 'pwsh -NoProfile -Command "Get-Content -Raw -LiteralPath ''.agents/skills/benchmark/SKILL.md''"'
$roslynKitContextReadCommand = 'pwsh -NoProfile -Command "Get-Content -Raw -LiteralPath ''.agents/skills/roslynkit/SKILL.md''; Get-Content -Raw -LiteralPath ''.agents/skills/roslynkit/references/commands.md''; Get-Content -Raw -LiteralPath ''.agents/skills/roslynkit/references/output.md''"'
Assert-True -Condition $prompt.Contains($benchmarkSkillReadCommand) -Message "The measured prompt did not wrap the required benchmark skill read with pwsh."
Assert-True -Condition $prompt.Contains($roslynKitContextReadCommand) -Message "The measured prompt did not wrap the required RoslynKit context reads with pwsh."
Assert-True -Condition $prompt.Contains("Never issue a bare PowerShell cmdlet") -Message "The measured prompt did not prohibit bare PowerShell cmdlets in host-dependent shells."
Assert-False -Condition $prompt.Contains("with Get-Content -Raw before investigating code") -Message "The measured prompt retained the ambiguous bare Get-Content instruction."
Assert-True -Condition $prompt.Contains("--target ./RoslynKit.slnx") -Message "The RoslynKit target path is not portable."
Assert-True -Condition $prompt.Contains("--index-path ./artifacts/roslynkit.db") -Message "The RoslynKit index path is not portable."
Assert-True -Condition $prompt.Contains("Do not run repository-root recursive searches") -Message "The common prompt did not prohibit repository-root recursive searches."
Assert-True -Condition (Test-RoslynKitInvocation -Command "/opt/tools/roslynkit version" -ResolvedRoslynKitPath "roslynkit") -Message "An absolute native RoslynKit path was not recognized."
Assert-False -Condition (Test-RoslynKitInvocation -Command "/opt/tools/not-roslynkit version" -ResolvedRoslynKitPath "roslynkit") -Message "An unrelated absolute path was misclassified as RoslynKit."
if (-not $IsWindows) {
    Assert-False -Condition (Test-RoslynKitInvocation -Command "/opt/tools/RoslynKit version" -ResolvedRoslynKitPath "roslynkit") -Message "A differently cased Linux executable path was misclassified as RoslynKit."
}

$disabledFeatures = @(Get-DisabledFeatures -DryRunMode)
Assert-True -Condition ($disabledFeatures -contains "unified_exec") -Message "Dry-run feature normalization did not disable unified_exec."
$filteredFeatures = @(Select-DisabledFeatures -Requested @("apps", "unified_exec") -Available @("apps"))
Assert-True -Condition ($filteredFeatures -contains "unified_exec") -Message "Runtime feature filtering made unified_exec conditional on feature-list output."
$codexArguments = New-CodexArguments -Prompt "Portability prompt." -RepoRoot "<repository-root>" -AnswerPath "<answer-path>" -DisabledFeatures $disabledFeatures
Assert-True -Condition ((Format-CommandLine -Arguments $codexArguments).Contains("--disable unified_exec")) -Message "Child Codex arguments did not disable unified_exec."

$remoteRawSkillRead = @'
/bin/bash -lc "pwsh -NoProfile -Command \"Get-Content -Raw -LiteralPath '.agents/skills/benchmark/SKILL.md'\""
'@
$remoteRawSkillRead = $remoteRawSkillRead.Trim()
$remoteRoslynContextRead = @'
/bin/bash -lc 'pwsh -NoProfile -Command "Get-Content -Raw .agents/skills/roslynkit/SKILL.md; Get-Content -Raw .agents/skills/roslynkit/references/commands.md; Get-Content -Raw .agents/skills/roslynkit/references/output.md"'
'@
$remoteRoslynContextRead = $remoteRoslynContextRead.Trim()
$remoteRoslynSearch = @'
/bin/bash -lc 'timeout 120s roslynkit search --target ./RoslynKit.slnx --index-path ./artifacts/roslynkit.db --query "tracked files change reload workspace" --max-results 5'
'@
$remoteRoslynSearch = $remoteRoslynSearch.Trim()
$remoteRoslynSource = @'
/bin/bash -lc "timeout 120s roslynkit symbol-source --target ./RoslynKit.slnx --symbol 'M:RoslynKit.WorkspaceDaemonSession.BeginReload'"
'@
$remoteRoslynSource = $remoteRoslynSource.Trim()

Assert-True -Condition (Test-CommandReadsContextPath -Command $remoteRawSkillRead -ContextPath ".agents/skills/benchmark/SKILL.md") -Message "The exact Remote-WSL benchmark skill read was not recognized."
Assert-True -Condition (Test-CommandReadsContextPath -Command $remoteRoslynContextRead -ContextPath ".agents/skills/roslynkit/SKILL.md") -Message "A Bash-wrapped stable skill read was not recognized."
Assert-True -Condition (Test-CommandReadsContextPath -Command $remoteRoslynContextRead -ContextPath ".agents/skills/roslynkit/references/commands.md") -Message "A Bash-wrapped command-reference read was not recognized."
Assert-True -Condition (Test-CommandReadsContextPath -Command $remoteRoslynContextRead -ContextPath ".agents/skills/roslynkit/references/output.md") -Message "A Bash-wrapped output-reference read was not recognized."
Assert-True -Condition (Test-CommandReadsContextPath -Command 'powershell.exe -NoProfile -Command "Get-Content -Raw .agents/skills/benchmark/SKILL.md"' -ContextPath ".agents/skills/benchmark/SKILL.md") -Message "A native Windows PowerShell skill read was not recognized."
Assert-True -Condition (Test-RoslynKitInvocation -Command $remoteRoslynSearch -ResolvedRoslynKitPath "roslynkit") -Message "The exact Remote-WSL timeout-wrapped RoslynKit search was not recognized."
Assert-True -Condition (Test-RoslynKitInvocation -Command $remoteRoslynSource -ResolvedRoslynKitPath "roslynkit") -Message "The exact Remote-WSL timeout-wrapped RoslynKit source command was not recognized."
Assert-True -Condition (Test-RoslynKitInvocation -Command "timeout 120s roslynkit version" -ResolvedRoslynKitPath "roslynkit") -Message "A direct GNU timeout wrapper was not recognized."
Assert-True -Condition (Test-RoslynKitInvocation -Command "pwsh -NoProfile -Command 'roslynkit version'" -ResolvedRoslynKitPath "roslynkit") -Message "A native PowerShell wrapper was not recognized."
Assert-True -Condition (Test-RoslynKitInvocation -Command "powershell.exe -NoProfile -Command 'roslynkit version'" -ResolvedRoslynKitPath "roslynkit") -Message "A native Windows PowerShell wrapper was not recognized."
Assert-True -Condition (Test-RoslynKitInvocation -Command 'cmd.exe /c "roslynkit version"' -ResolvedRoslynKitPath "roslynkit") -Message "A cmd /c wrapper was not recognized."
Assert-True -Condition (Test-RoslynKitInvocation -Command 'cmd.exe /d /s /c "roslynkit version"' -ResolvedRoslynKitPath "roslynkit") -Message "A cmd /c wrapper with standard options was not recognized."
Assert-True -Condition (Test-RoslynKitInvocation -Command "/bin/sh -c 'roslynkit version'" -ResolvedRoslynKitPath "roslynkit") -Message "A sh -c wrapper was not recognized."
Assert-True -Condition (Test-RoslynKitInvocation -Command "/usr/bin/zsh -lc 'roslynkit version'" -ResolvedRoslynKitPath "roslynkit") -Message "A zsh -lc wrapper was not recognized."

$nestedSkillRead = @'
pwsh -NoProfile -Command "bash -lc 'pwsh -NoProfile -Command \"Get-Content -Raw .agents/skills/benchmark/SKILL.md\"'"
'@
$nestedSkillRead = $nestedSkillRead.Trim()
Assert-True -Condition (Test-CommandReadsContextPath -Command $nestedSkillRead -ContextPath ".agents/skills/benchmark/SKILL.md") -Message "A nested PowerShell, Bash, and PowerShell skill read was not recognized."

$quotedBashMention = @'
/bin/bash -lc 'printf "%s\n" "roslynkit search"'
'@
$quotedBashMention = $quotedBashMention.Trim()
Assert-False -Condition (Test-RoslynKitInvocation -Command '"roslynkit search"' -ResolvedRoslynKitPath "roslynkit") -Message "A quoted RoslynKit search phrase was mistaken for execution."
Assert-False -Condition (Test-RoslynKitInvocation -Command 'Write-Output "roslynkit search"' -ResolvedRoslynKitPath "roslynkit") -Message "Quoted PowerShell text was mistaken for a RoslynKit invocation."
Assert-False -Condition (Test-RoslynKitInvocation -Command $quotedBashMention -ResolvedRoslynKitPath "roslynkit") -Message "Quoted Bash text was mistaken for a RoslynKit invocation."
Assert-False -Condition (Test-RoslynKitInvocation -Command '/bin/bash -lc ''rg -n "roslynkit search" .''' -ResolvedRoslynKitPath "roslynkit") -Message "Quoted ripgrep search text was mistaken for a RoslynKit invocation."
Assert-False -Condition (Test-RoslynKitInvocation -Command 'cmd.exe /c "echo roslynkit search"' -ResolvedRoslynKitPath "roslynkit") -Message "Quoted cmd output text was mistaken for a RoslynKit invocation."
Assert-False -Condition (Test-RoslynKitInvocation -Command "/bin/bash -lc '/opt/tools/not-roslynkit version'" -ResolvedRoslynKitPath "roslynkit") -Message "A wrapped unrelated executable path was mistaken for RoslynKit."

$failedRawRootSearch = @'
/bin/bash -lc "rg -n -i \"compiler view|tracked files|reload|code navigation|navigation request|workspace.*change|file.*change\" --glob '"'"'! .agents/skills/**'"'"' --glob '"'"'!**/bin/**'"'"' --glob '"'"'!**/obj/**'"'"' ."
'@
$failedRawRootSearch = $failedRawRootSearch.Trim()
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command $failedRawRootSearch) -Message "The exact failed Remote-WSL repository-root search was not detected."
$excludedRawRootSearch = '/bin/bash -lc ''rg -n "reload" --glob "!*.md" .'''
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command $excludedRawRootSearch) -Message "A Bash-wrapped repository-root search with exclusions was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''rg -n reload''') -Message "An implicit ripgrep repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''rg --files''') -Message "An implicit ripgrep file enumeration was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''rg -n reload "$PWD"''') -Message "A PWD-expanded repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'pwsh -NoProfile -Command ''rg -n reload $PWD.Path''' -RepoRoot $repoRoot) -Message "A PowerShell PWD.Path repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'pwsh -NoProfile -Command ''rg -n reload $(Get-Location)''' -RepoRoot $repoRoot) -Message "A PowerShell Get-Location repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''find -name "*.cs"''') -Message "An implicit find repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command "rg -n reload '$repoRoot'" -RepoRoot $repoRoot) -Message "An absolute repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'rg -n reload ./src/..' -RepoRoot $repoRoot) -Message "A normalized relative repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'rg -n reload ../roslynkit' -RepoRoot $repoRoot) -Message "A parent-relative repository-root search was accepted."
$nativeRepoRoot = $repoRoot -replace '(?i)^(?:Microsoft\.PowerShell\.Core\\)?FileSystem::', ''
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command "rg -n reload 'FileSystem::$nativeRepoRoot'" -RepoRoot $repoRoot) -Message "A FileSystem-provider repository-root search was accepted."
$syntheticHomeRepoRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) "repos/roslynkit"
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'rg -n reload ~/repos/roslynkit' -RepoRoot $syntheticHomeRepoRoot) -Message "A tilde-expanded repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'rg -n reload $HOME/repos/roslynkit' -RepoRoot $syntheticHomeRepoRoot) -Message "A HOME-expanded repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'cmd.exe /c "rg -n reload %USERPROFILE%\repos\roslynkit"' -RepoRoot $syntheticHomeRepoRoot) -Message "A USERPROFILE-expanded repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''fd reload''') -Message "An implicit fd repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'pwsh -NoProfile -Command ''Get-ChildItem -Recurse''') -Message "An implicit PowerShell repository-root enumeration was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'pwsh -NoProfile -Command ''rg -n "reload" .''') -Message "A PowerShell-wrapped repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'cmd.exe /c "rg -n reload ."') -Message "A cmd-wrapped repository-root search was accepted."
Assert-True -Condition (Test-RepositoryRootRecursiveSearch -Command 'cmd.exe /c "dir /s ."') -Message "A recursive cmd repository-root enumeration was accepted."
Assert-False -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''rg -n "reload" src/RoslynKit tests/RoslynKit.Tests''') -Message "A bounded source-and-test search was mistaken for a repository-root search."
Assert-False -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''rg --files src/RoslynKit tests/RoslynKit.Tests''') -Message "A bounded ripgrep file enumeration was mistaken for a repository-root search."
Assert-False -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''rg -n "." src/RoslynKit tests/RoslynKit.Tests''') -Message "A bounded ripgrep period-pattern search was mistaken for a repository-root search."
Assert-False -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''grep -R "." src/RoslynKit''') -Message "A bounded grep period-pattern search was mistaken for a repository-root search."
Assert-False -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''fd "." src/RoslynKit''') -Message "A bounded fd period-pattern search was mistaken for a repository-root search."
Assert-False -Condition (Test-RepositoryRootRecursiveSearch -Command 'pwsh -NoProfile -Command ''Get-ChildItem -Recurse -File src/RoslynKit''') -Message "A bounded PowerShell recursive enumeration was mistaken for a repository-root search."
Assert-False -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''find src/RoslynKit tests/RoslynKit.Tests -name "*.cs"''') -Message "A bounded find command was mistaken for a repository-root search."
Assert-False -Condition (Test-RepositoryRootRecursiveSearch -Command 'rg --version') -Message "A ripgrep version probe was mistaken for a repository-root search."
Assert-False -Condition (Test-RepositoryRootRecursiveSearch -Command '/bin/bash -lc ''printf "%s\n" "rg -n reload ."''') -Message "Quoted repository-root search text was mistaken for execution."
$failedRawRootEvents = @(
    (New-BenchmarkCommandEvent -Type "item.completed" -Id "raw-root-1" -Command $failedRawRootSearch)
)
$failedRawRootIssues = @(Get-ComplianceIssues -Condition "raw-codex" -Commands @($failedRawRootSearch) -Events $failedRawRootEvents -RepositoryChanges @() -ResolvedRoslynKitPath "roslynkit")
Assert-True -Condition (($failedRawRootIssues -join "; ") -match "used forbidden context surface") -Message "The exact failed raw command replay did not fail compliance."
$excludedRawRootIssues = @(Get-ComplianceIssues -Condition "raw-codex" -Commands @($excludedRawRootSearch) -Events @() -RepositoryChanges @() -ResolvedRoslynKitPath "roslynkit")
Assert-True -Condition (($excludedRawRootIssues -join "; ") -match "used forbidden context surface") -Message "A repository-root search without a named forbidden path did not fail compliance."
$implicitRawRootIssues = @(Get-ComplianceIssues -Condition "raw-codex" -Commands @('/bin/bash -lc ''rg -n reload''') -Events @() -RepositoryChanges @() -ResolvedRoslynKitPath "roslynkit" -RepoRoot $repoRoot)
Assert-True -Condition (($implicitRawRootIssues -join "; ") -match "used forbidden context surface") -Message "An implicit repository-root search did not fail compliance."

$overlappingEvents = @(
    (New-BenchmarkCommandEvent -Type "item.started" -Id "rk-1" -Command $remoteRoslynSearch -Status "in_progress" -ExitCode $null),
    (New-BenchmarkCommandEvent -Type "item.started" -Id "rk-2" -Command $remoteRoslynSource -Status "in_progress" -ExitCode $null)
)
Assert-True -Condition (Test-ConcurrentRoslynKitInvocations -Events $overlappingEvents -ResolvedRoslynKitPath "roslynkit") -Message "Overlapping Bash-wrapped RoslynKit commands were not detected."
$serialEvents = @(
    (New-BenchmarkCommandEvent -Type "item.started" -Id "rk-1" -Command $remoteRoslynSearch -Status "in_progress" -ExitCode $null),
    (New-BenchmarkCommandEvent -Type "item.completed" -Id "rk-1" -Command $remoteRoslynSearch),
    (New-BenchmarkCommandEvent -Type "item.started" -Id "rk-2" -Command $remoteRoslynSource -Status "in_progress" -ExitCode $null),
    (New-BenchmarkCommandEvent -Type "item.completed" -Id "rk-2" -Command $remoteRoslynSource)
)
Assert-False -Condition (Test-ConcurrentRoslynKitInvocations -Events $serialEvents -ResolvedRoslynKitPath "roslynkit") -Message "Serial Bash-wrapped RoslynKit commands were classified as overlapping."

$rawCommands = @(
    $remoteRawSkillRead,
    '/bin/bash -lc ''rg -n "reload|snapshot" --glob "*.cs" src/RoslynKit tests/RoslynKit.Tests'''
)
$rawEvents = @(
    (New-BenchmarkCommandEvent -Type "item.completed" -Id "raw-1" -Command $rawCommands[0]),
    (New-BenchmarkCommandEvent -Type "item.completed" -Id "raw-2" -Command $rawCommands[1])
)
$rawIssues = @(Get-ComplianceIssues -Condition "raw-codex" -Commands $rawCommands -Events $rawEvents -RepositoryChanges @() -ResolvedRoslynKitPath "roslynkit")
Assert-True -Condition ($rawIssues.Count -eq 0) -Message "The previously invalid Remote-WSL raw sequence still failed compliance: $($rawIssues -join '; ')"

$roslynKitCommands = @($remoteRawSkillRead, $remoteRoslynContextRead, $remoteRoslynSearch, $remoteRoslynSource)
$roslynKitEvents = @()
for ($commandIndex = 0; $commandIndex -lt $roslynKitCommands.Count; $commandIndex++) {
    $roslynKitEvents += New-BenchmarkCommandEvent -Type "item.completed" -Id "roslynkit-$commandIndex" -Command $roslynKitCommands[$commandIndex]
}
$roslynKitIssues = @(Get-ComplianceIssues -Condition "roslynkit" -Commands $roslynKitCommands -Events $roslynKitEvents -RepositoryChanges @() -ResolvedRoslynKitPath "roslynkit")
Assert-True -Condition ($roslynKitIssues.Count -eq 0) -Message "The previously invalid Remote-WSL RoslynKit sequence still failed compliance: $($roslynKitIssues -join '; ')"

$testApplicationPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Path
$validProbe = [pscustomobject]@{
    schema_version = 1
    host_kind = $hostKind
    ripgrep = [pscustomobject]@{ resolved_path = $testApplicationPath; output = "ripgrep 15.2.0"; exit_code = 0 }
    roslynkit = [pscustomobject]@{ resolved_path = $testApplicationPath; output = "roslynkit version 0.2.0"; exit_code = 0 }
}
Assert-True -Condition (@(Get-ToolProbeValidationIssues -Probe $validProbe).Count -eq 0) -Message "A valid structured tool probe was rejected."
$missingToolProbe = [pscustomobject]@{
    schema_version = 1
    host_kind = $hostKind
    ripgrep = $null
    roslynkit = $validProbe.roslynkit
}
$missingToolIssues = @(Get-ToolProbeValidationIssues -Probe $missingToolProbe)
Assert-True -Condition (($missingToolIssues -join "; ") -match "ripgrep probe was missing") -Message "A missing tool probe was accepted."
$nonzeroProbe = [pscustomobject]@{
    schema_version = 1
    host_kind = $hostKind
    ripgrep = [pscustomobject]@{ resolved_path = $testApplicationPath; output = "ripgrep 15.2.0"; exit_code = 7 }
    roslynkit = $validProbe.roslynkit
}
$nonzeroProbeIssues = @(Get-ToolProbeValidationIssues -Probe $nonzeroProbe)
Assert-True -Condition (($nonzeroProbeIssues -join "; ") -match "ripgrep exit code was not zero") -Message "A nonzero individual tool exit was accepted."

$successfulPreflightEvents = @(
    (New-BenchmarkCommandEvent -Type "item.completed" -Id "probe-1" -Command "pwsh -NoProfile -File ./scripts/benchmark-codex.ps1 -InternalToolProbePath ./artifacts/probe.json")
)
Assert-True -Condition (Test-SingleSuccessfulCommandEvent -Events $successfulPreflightEvents) -Message "One successful child probe event was rejected."
$failedPreflightEvents = @(
    (New-BenchmarkCommandEvent -Type "item.completed" -Id "probe-1" -Command "pwsh -NoProfile -File ./scripts/benchmark-codex.ps1 -InternalToolProbePath ./artifacts/probe.json" -Status "failed" -ExitCode 1)
)
Assert-False -Condition (Test-SingleSuccessfulCommandEvent -Events $failedPreflightEvents) -Message "A failed child probe event was accepted."
Assert-False -Condition (Test-SingleSuccessfulCommandEvent -Events @()) -Message "A missing child probe event was accepted."
Assert-False -Condition (Test-SingleSuccessfulCommandEvent -Events @($successfulPreflightEvents + $successfulPreflightEvents)) -Message "Multiple child probe events were accepted."

$probeTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("roslynkit-tool-probe-" + [Guid]::NewGuid())
$validProbePath = Join-Path $probeTestRoot "tool-probe.json"
$invalidProbePath = Join-Path $probeTestRoot "invalid.json"
$emittedProbePath = Join-Path $probeTestRoot "emitted.json"
try {
    New-Item -ItemType Directory -Path $probeTestRoot | Out-Null
    & $testApplicationPath -NoProfile -ExecutionPolicy Bypass -File $benchmarkScriptPath -InternalToolProbePath $emittedProbePath
    Assert-True -Condition ($LASTEXITCODE -eq 0) -Message "The hidden tool-probe process failed."
    Assert-True -Condition (Test-Path -LiteralPath $emittedProbePath -PathType Leaf) -Message "The hidden tool-probe process did not write its artifact."
    $emittedProbe = Get-Content -Raw -LiteralPath $emittedProbePath | ConvertFrom-Json
    Assert-True -Condition ($emittedProbe.schema_version -eq 1 -and $emittedProbe.host_kind -eq $hostKind) -Message "The hidden tool-probe artifact had invalid host metadata."
    foreach ($toolName in @("ripgrep", "roslynkit")) {
        $toolProbe = $emittedProbe.PSObject.Properties[$toolName].Value
        $toolProperties = @($toolProbe.PSObject.Properties.Name)
        Assert-True -Condition ($toolProperties -contains "resolved_path" -and $toolProperties -contains "output" -and $toolProperties -contains "exit_code") -Message "The hidden $toolName probe omitted required fields."
    }
    $validProbe | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $validProbePath -Encoding UTF8
    $validatedProbe = Read-ValidatedToolProbe -Path $validProbePath
    Assert-True -Condition ($validatedProbe.host_kind -eq $hostKind) -Message "The validated tool probe lost its host classification."
    Set-Content -LiteralPath $invalidProbePath -Value "{" -NoNewline
    Assert-Throws -Action { Read-ValidatedToolProbe -Path $invalidProbePath } -Message "Malformed tool-probe JSON was accepted."
    Assert-Throws -Action { Read-ValidatedToolProbe -Path (Join-Path $probeTestRoot "missing.json") } -Message "A missing tool-probe artifact was accepted."
}
finally {
    if (Test-Path -LiteralPath $probeTestRoot) {
        Remove-Item -LiteralPath $probeTestRoot -Recurse -Force
    }
}

Write-Host "PowerShell portability regression tests passed."
