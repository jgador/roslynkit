[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Model = "gpt-5.6-sol",
    [ValidateNotNullOrEmpty()]
    [string] $ReasoningEffort = "high",
    [ValidateRange(1, 100)]
    [int] $Trials = 1,
    [string] $CaseId = "all",
    [string] $RoslynKitPath,
    [switch] $DryRun,
    [switch] $KeepSnapshot
)
$ErrorActionPreference = "Stop"
function Resolve-RepoRoot {
    $root = & git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) { throw "Run the benchmark from a Git worktree." }
    return (Resolve-Path -LiteralPath $root).Path
}
function Resolve-RoslynKitPath {
    param([string] $Candidate)
    if (-not [string]::IsNullOrWhiteSpace($Candidate)) {
        if (Test-Path -LiteralPath $Candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $Candidate).Path
        }
        return $Candidate
    }
    foreach ($name in @("roslynkit-dev")) {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue
        if ($null -ne $command) { return $(if ($command.Path) { $command.Path } else { $command.Source }) }
    }
    $executable = if ($env:OS -eq "Windows_NT") { "roslynkit.exe" } else { "roslynkit" }
    $conventionalPath = Join-Path $HOME ".roslynkit\tools\roslynkit-dev\$executable"
    if (Test-Path -LiteralPath $conventionalPath -PathType Leaf) { return (Resolve-Path -LiteralPath $conventionalPath).Path }
    $command = Get-Command roslynkit -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $(if ($command.Path) { $command.Path } else { $command.Source }) }
    return "roslynkit"
}
function Test-RoslynKitPath {
    param([string] $Path)
    return (Test-Path -LiteralPath $Path -PathType Leaf) -or ($null -ne (Get-Command $Path -ErrorAction SilentlyContinue))
}
function Get-CaseData {
    param([string] $RepoRoot)
    $path = Join-Path $RepoRoot "benchmarks\codex-cases.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Benchmark cases were not found at '$path'."
    }
    $cases = @((Get-Content -Raw -LiteralPath $path | ConvertFrom-Json).cases)
    if ($cases.Count -ne 3 -or @($cases | Where-Object { [string]::IsNullOrWhiteSpace($_.id) -or [string]::IsNullOrWhiteSpace($_.prompt) }).Count -gt 0) {
        throw "Benchmark case data must contain the three named prompts."
    }
    return $cases
}
function Get-SelectedCases {
    param([object[]] $Cases, [string] $SelectedCaseId)
    if ($SelectedCaseId -eq "all") {
        return $Cases
    }
    $selected = @($Cases | Where-Object { $_.id -eq $SelectedCaseId })
    if ($selected.Count -ne 1) {
        throw "No benchmark case matches CaseId='$SelectedCaseId'."
    }
    return $selected
}
function New-ArmPrompt {
    param([string] $Arm, [string] $Prompt, [string] $ResolvedRoslynKitPath)
    $rules = @(
        "Read-only benchmark arm: $Arm.",
        "Do not edit files or change Git state.",
        "Do not run builds, restores, tests, or other commands that write caches; inspect test source instead.",
        "Do not use web search, browsers, network requests, subagents, memory, prior-session files, Atlas, CODEX_HOME, .codex, .agents, or AGENTS.md.",
        "Do not inspect benchmark scripts, benchmark data, or benchmark documentation.",
        "Return concise source-and-test evidence; do not change files."
    )
    if ($Arm -eq "raw-codex") {
        $rules += "Use ordinary local shell and text inspection only. Do not invoke RoslynKit, roslynkit-dev, or dotnet run for RoslynKit."
    }
    else {
        $rules += "Use this RoslynKit executable for code investigation: '$ResolvedRoslynKitPath'."
        $rules += "Pass --target .\RoslynKit.slnx to RoslynKit. The prepared read-only search index is .\.benchmark\roslynkit.db; pass --index-path .\.benchmark\roslynkit.db to search."
    }
    return (($rules + "" + $Prompt) -join [Environment]::NewLine)
}
function New-CodexArguments {
    param([string] $Prompt, [string] $SnapshotPath, [string] $AnswerPath, [string[]] $DisabledFeatures)
    $arguments = @(
        "exec", "--config", 'approval_policy="never"', "--config", ('model_reasoning_effort="{0}"' -f $ReasoningEffort),
        "--config", "project_doc_max_bytes=0", "--config", "memories.use_memories=false", "--config", "memories.generate_memories=false",
        "--config", 'shell_environment_policy.inherit="core"',
        "--config", "shell_environment_policy.ignore_default_excludes=false", "--model", $Model, "--sandbox", "read-only",
        "--ephemeral", "--ignore-user-config", "--ignore-rules", "--json", "--color", "never", "--cd", $SnapshotPath, "--output-last-message", $AnswerPath
    )
    foreach ($feature in $DisabledFeatures) { $arguments += "--disable", $feature }
    $arguments += $Prompt; return $arguments
}
function Get-DisabledFeatures {
    param([switch] $DryRunMode)
    $requested = @("apps", "browser_use", "browser_use_external", "browser_use_full_cdp_access", "computer_use", "external_agent_memory_import", "goals", "hooks", "image_generation", "in_app_browser", "memories", "multi_agent", "multi_agent_v2", "plugin_sharing", "plugins", "remote_plugin", "shell_snapshot", "skill_mcp_dependency_install", "skill_search", "standalone_web_search", "workspace_dependencies")
    if ($DryRunMode) { return $requested }
    $featureLines = @(& codex features list)
    if ($LASTEXITCODE -ne 0) { throw "The installed Codex CLI could not enumerate features for clean-room isolation." }
    $available = @($featureLines | ForEach-Object { ($_ -split '\s+')[0] })
    return @($requested | Where-Object { $available -contains $_ })
}
function Format-CommandLine {
    param([string[]] $Arguments)
    return (($Arguments | ForEach-Object {
                if ($_ -match '^[A-Za-z0-9_./:\\=-]+$') { $_ } else { "'" + $_.Replace("'", "''") + "'" }
            }) -join " ")
}
function Test-ExcludedSnapshotFile {
    param([string] $RelativePath)
    $path = $RelativePath.Replace("\\", "/")
    return $path -match "(^|/)(bin|obj)(/|$)" -or
        $path -match "(^|/)(\.agents|\.codex|\.claude|artifacts)(/|$)" -or
        $path -match "(^|/)(AGENTS|MEMORY|SKILL)\.md$" -or
        $path -match "(^|/)\.github/(skills(/|$)|copilot-instructions\.md$)" -or
        $path -match "^docs/agents(/|$)" -or
        $path -match "^benchmarks(/|$)" -or
        $path -match "^scripts/(benchmark-.*|measure-.*benchmark.*)\.ps1$" -or
        $path -match "^docs/.*benchmark.*\.md$" -or
        $path -eq "docs/local-repository-reference.md"
}
function New-CleanSnapshot {
    param([string] $RepoRoot, [string] $TemporaryRoot, [string] $SnapshotName = "snapshot")
    $snapshot = Join-Path $TemporaryRoot $SnapshotName
    New-Item -ItemType Directory -Force -Path $snapshot | Out-Null
    $files = @(& git -C $RepoRoot ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list nonignored repository files for the benchmark snapshot."
    }
    foreach ($relativePath in $files) {
        if (Test-ExcludedSnapshotFile -RelativePath $relativePath) {
            continue
        }
        $source = Join-Path $RepoRoot $relativePath
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            continue
        }
        $destination = Join-Path $snapshot $relativePath
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    & git -C $snapshot init --quiet
    & git -C $snapshot config user.name "Codex benchmark"
    & git -C $snapshot config user.email "codex-benchmark@localhost"
    & git -C $snapshot config core.autocrlf false
    & git -C $snapshot config core.eol lf
    & git -C $snapshot config core.safecrlf false
    & git -C $snapshot config commit.gpgSign false
    $emptyHooksPath = Join-Path $snapshot ".git\benchmark-hooks"
    New-Item -ItemType Directory -Force -Path $emptyHooksPath | Out-Null
    & git -C $snapshot config core.hooksPath $emptyHooksPath
    & git -C $snapshot add --all --force
    & git -C $snapshot commit --quiet --message "benchmark snapshot"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not commit the clean benchmark snapshot."
    }
    return $snapshot
}
function Restore-SnapshotDependencies {
    param([string] $SnapshotPath)
    $solution = Join-Path $SnapshotPath "RoslynKit.slnx"
    & dotnet restore $solution --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Snapshot restore failed before measured runs."
    }
}
function Initialize-RoslynKitIndex {
    param([string] $SnapshotPath, [string] $ResolvedRoslynKitPath)
    $excludePath = Join-Path $SnapshotPath ".git\info\exclude"
    Add-Content -LiteralPath $excludePath -Value ".benchmark/" -Encoding ascii
    Push-Location $SnapshotPath
    try {
        & $ResolvedRoslynKitPath index --target ".\RoslynKit.slnx" --index-path ".\.benchmark\roslynkit.db"
        if ($LASTEXITCODE -ne 0) {
            throw "RoslynKit index preparation failed before measured runs."
        }
    }
    finally {
        Pop-Location
    }
}
function Stop-RoslynKitDaemon {
    param([string] $SnapshotPath, [string] $ResolvedRoslynKitPath)
    if ([string]::IsNullOrWhiteSpace($SnapshotPath) -or -not (Test-Path -LiteralPath (Join-Path $SnapshotPath "RoslynKit.slnx") -PathType Leaf)) {
        return
    }
    Push-Location $SnapshotPath
    try {
        $stopOutput = @(& $ResolvedRoslynKitPath daemon stop --target ".\RoslynKit.slnx" 2>&1)
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "RoslynKit daemon cleanup failed for '$SnapshotPath': $($stopOutput -join ' ')"
            return
        }
        if (($stopOutput -join "`n") -match "state: not-running") {
            return
        }
        foreach ($attempt in 1..20) {
            $statusOutput = @(& $ResolvedRoslynKitPath daemon status --target ".\RoslynKit.slnx" 2>&1)
            if ($LASTEXITCODE -eq 0 -and ($statusOutput -join "`n") -match "state: not-running") {
                return
            }
            Start-Sleep -Milliseconds 250
        }
        Write-Warning "RoslynKit daemon did not stop within five seconds for '$SnapshotPath'."
    }
    catch {
        Write-Warning "RoslynKit daemon cleanup failed for '$SnapshotPath': $($_.Exception.Message)"
    }
    finally {
        Pop-Location
    }
}
function Get-CodexHome {
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        return $env:CODEX_HOME
    }
    return (Join-Path $HOME ".codex")
}
function New-AuthenticationSeed {
    param([string] $TemporaryRoot, [string] $SourceCodexHome)
    $seedDirectory = Join-Path $TemporaryRoot "auth"
    $seedPath = Join-Path $seedDirectory "auth.json"
    New-Item -ItemType Directory -Force -Path $seedDirectory | Out-Null
    $sourcePath = Join-Path $SourceCodexHome "auth.json"
    if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
        Copy-Item -LiteralPath $sourcePath -Destination $seedPath -Force
    }
    return $seedPath
}
function Set-ChildHome {
    param([string] $ChildHome, [string] $AuthenticationSeedPath)
    New-Item -ItemType Directory -Force -Path $ChildHome | Out-Null
    $childCodexHome = Join-Path $ChildHome ".codex"
    New-Item -ItemType Directory -Force -Path $childCodexHome | Out-Null
    if (Test-Path -LiteralPath $AuthenticationSeedPath -PathType Leaf) {
        Copy-Item -LiteralPath $AuthenticationSeedPath -Destination (Join-Path $childCodexHome "auth.json") -Force
    }
    $names = @("CODEX_HOME", "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "CODEX_THREAD_ID")
    $previous = @{}
    foreach ($name in $names) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    $homeDrive = Split-Path -Qualifier $ChildHome
    [Environment]::SetEnvironmentVariable("CODEX_HOME", $childCodexHome, "Process")
    [Environment]::SetEnvironmentVariable("HOME", $ChildHome, "Process")
    [Environment]::SetEnvironmentVariable("USERPROFILE", $ChildHome, "Process")
    [Environment]::SetEnvironmentVariable("HOMEDRIVE", $homeDrive, "Process")
    [Environment]::SetEnvironmentVariable("HOMEPATH", $ChildHome.Substring($homeDrive.Length), "Process")
    [Environment]::SetEnvironmentVariable("CODEX_THREAD_ID", $null, "Process")
    return $previous
}
function Update-AuthenticationSeed {
    param([string] $ChildHome, [string] $AuthenticationSeedPath)
    $childAuthPath = Join-Path $ChildHome ".codex\auth.json"
    if (Test-Path -LiteralPath $childAuthPath -PathType Leaf) {
        Copy-Item -LiteralPath $childAuthPath -Destination $AuthenticationSeedPath -Force
    }
}
function Restore-ChildHome {
    param([hashtable] $Previous)
    foreach ($name in $Previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $Previous[$name], "Process")
    }
}
function Remove-ManagedDirectory {
    param([string] $Path, [string] $ParentPath)
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $trimCharacters = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path.TrimEnd($trimCharacters)
    $resolvedParent = (Resolve-Path -LiteralPath $ParentPath).Path.TrimEnd($trimCharacters)
    $parentPrefix = $resolvedParent + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete a directory outside the managed benchmark area: '$resolvedPath'."
    }
    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}
function Read-Events {
    param([string] $Path)
    $events = New-Object System.Collections.Generic.List[object]
    foreach ($line in Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue) {
        try {
            $events.Add(($line | ConvertFrom-Json))
        }
        catch {
            # Event stdout must be JSONL; malformed lines are ignored but token data may then be absent.
        }
    }
    return @($events)
}
function Get-TokenUsage {
    param([object[]] $Events)
    $usage = $null
    foreach ($event in $Events) {
        if ($event.type -eq "turn.completed" -and $null -ne $event.usage -and $null -ne $event.usage.input_tokens) {
            $usage = $event.usage
        }
        elseif ($null -ne $event.payload -and $event.payload.type -eq "token_count" -and $null -ne $event.payload.info.total_token_usage -and $null -ne $event.payload.info.total_token_usage.input_tokens) {
            $usage = $event.payload.info.total_token_usage
        }
    }
    return $usage
}
function Get-Commands {
    param([object[]] $Events)
    $commands = New-Object System.Collections.Generic.List[string]
    foreach ($event in $Events) {
        if ($null -ne $event.item -and $event.item.type -eq "command_execution" -and -not [string]::IsNullOrWhiteSpace($event.item.command)) {
            $commands.Add([string] $event.item.command)
            continue
        }
        if ($event.type -eq "response_item" -and $null -ne $event.payload -and $event.payload.type -eq "function_call" -and
            $event.payload.name -in @("shell_command", "exec_command", "shell")) {
            try {
                $arguments = $event.payload.arguments | ConvertFrom-Json
                if (-not [string]::IsNullOrWhiteSpace($arguments.command)) {
                    $commands.Add([string] $arguments.command)
                }
            }
            catch {
                # Command events remain the preferred source when function arguments are malformed.
            }
        }
    }
    return @($commands | Select-Object -Unique)
}
function Test-RoslynKitInvocation {
    param([string] $Command, [string] $ResolvedRoslynKitPath)
    if ([string]::IsNullOrWhiteSpace($Command)) { return $false }
    $path = [regex]::Escape($ResolvedRoslynKitPath.Replace("/", "\\"))
    $file = [regex]::Escape([IO.Path]::GetFileName($ResolvedRoslynKitPath))
    $prefix = '(?i)(^|[;&|]\s*)(?:&\s*)?'
    $quote = '["'']?'
    $namedPattern = $prefix + '(?:' + $quote + $path + $quote + '|' + $quote + $file + $quote + '|roslynkit(?:-dev)?(?:\.exe)?)\s+\S+'
    $dotnetPattern = '(?i)(^|[;&|]\s*)dotnet(?:\.exe)?\s+run\b(?=[^\r\n]*--project\s+' + $quote + '[^;\r\n]*src[\\/]RoslynKit(?:[\\/]|["'']|\s))'
    return $Command -match $namedPattern -or $Command -match $dotnetPattern
}
function Get-ComplianceIssues {
    param([string] $Arm, [string[]] $Commands, [object[]] $Events, [string[]] $SnapshotChanges, [string] $ResolvedRoslynKitPath)
    $issues = New-Object System.Collections.Generic.List[string]
    $usedRoslynKit = $false
    foreach ($command in $Commands) {
        $usesRoslynKit = Test-RoslynKitInvocation -Command $command -ResolvedRoslynKitPath $ResolvedRoslynKitPath
        $usedRoslynKit = $usedRoslynKit -or $usesRoslynKit
        if ($Arm -eq "raw-codex" -and $usesRoslynKit) {
            $issues.Add("raw-codex invoked RoslynKit: $command")
        }
        if ($command -match "(?i)\b(curl|wget|Invoke-WebRequest|Invoke-RestMethod)\b|https?://") {
            $issues.Add("used web or network access: $command")
        }
        if ($command -match "(?i)(CODEX_HOME|\.codex|\.agents|AGENTS\.md|MEMORY\.md|rollout-|history\.jsonl|atlas-(csharp|doc|test)-mapper|codex-benchmark|benchmark-codex|codex-cases\.json|token-efficiency-benchmark)") {
            $issues.Add("used forbidden context surface: $command")
        }
        if ($command -match "(?i)\b(apply_patch|Set-Content|Add-Content|Out-File|New-Item|Remove-Item|Move-Item|Copy-Item)\b|git\s+(add|commit|checkout|switch|reset|restore|clean|stash)\b|\b(dotnet|msbuild)\s+(build|test|restore|pack|run)\b") {
            $issues.Add("attempted an edit: $command")
        }
    }
    foreach ($event in $Events) {
        $eventSurface = @([string] $event.type, [string] $event.item.type, [string] $event.item.name)
        if ($null -ne $event.payload -and $event.payload.type -eq "function_call") { $eventSurface += [string] $event.payload.name }
        $eventText = $eventSurface -join " "
        if ($eventText -match "(?i)(web_search|browser|computer|mcp|atlas|scout|explorer|worker|subagent|multi_agent|spawn_agent|memory)") {
            $issues.Add("used forbidden event surface: $eventText")
        }
    }
    if ($Arm -eq "roslynkit" -and -not $usedRoslynKit) {
        $issues.Add("roslynkit arm did not invoke RoslynKit")
    }
    if ($Commands.Count -eq 0) {
        $issues.Add("run recorded no inspection commands")
    }
    if ($SnapshotChanges.Count -gt 0) {
        $issues.Add("snapshot changed: $($SnapshotChanges -join '; ')")
    }
    return @($issues | Select-Object -Unique)
}
function Test-NonEmptyFile {
    param([string] $Path)
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and -not [string]::IsNullOrWhiteSpace((Get-Content -Raw -LiteralPath $Path))
}
function Get-SnapshotState {
    param([string] $SnapshotPath)
    return @(& git -C $SnapshotPath status --porcelain --ignored --untracked-files=all)
}
function Get-SnapshotChanges {
    param([string] $SnapshotPath, [string[]] $Baseline)
    return @(Compare-Object -ReferenceObject $Baseline -DifferenceObject (Get-SnapshotState -SnapshotPath $SnapshotPath) | ForEach-Object { $_.InputObject })
}
function Invoke-BenchmarkRun {
    param([object] $Case, [string] $Arm, [int] $Trial, [string] $SnapshotPath, [string[]] $SnapshotBaseline, [string] $RunRoot, [string] $TemporaryRoot, [string] $AuthenticationSeedPath, [string] $ResolvedRoslynKitPath, [string[]] $DisabledFeatures)
    $runId = "{0}-{1}-trial{2}" -f $Case.id, $Arm, $Trial
    $answerPath = Join-Path $RunRoot "answers\$runId.md"
    $eventPath = Join-Path $RunRoot "events\$runId.jsonl"
    $stderrPath = Join-Path $RunRoot "stderr\$runId.txt"
    $commandsPath = Join-Path $RunRoot "commands\$runId.txt"
    $prompt = New-ArmPrompt -Arm $Arm -Prompt $Case.prompt -ResolvedRoslynKitPath $ResolvedRoslynKitPath
    $arguments = New-CodexArguments -Prompt $prompt -SnapshotPath $SnapshotPath -AnswerPath $answerPath -DisabledFeatures $DisabledFeatures
    $childRoot = Join-Path $TemporaryRoot "homes"
    $childHome = Join-Path $childRoot ([Guid]::NewGuid().ToString("N"))
    $exitCode = -1
    $startedAt = [DateTime]::UtcNow
    $completedAt = $startedAt
    $previous = $null
    if ((Get-SnapshotChanges -SnapshotPath $SnapshotPath -Baseline $SnapshotBaseline).Count -gt 0) {
        throw "The prepared snapshot is dirty before '$runId'."
    }
    try {
        $previous = Set-ChildHome -ChildHome $childHome -AuthenticationSeedPath $AuthenticationSeedPath
        $startedAt = [DateTime]::UtcNow
        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        Push-Location $SnapshotPath
        try {
            & codex @arguments 1> $eventPath 2> $stderrPath
            $completedAt = [DateTime]::UtcNow
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
            $ErrorActionPreference = $oldPreference
        }
    }
    catch {
        $completedAt = [DateTime]::UtcNow
        $_ | Out-String | Set-Content -LiteralPath $stderrPath -Encoding UTF8
    }
    finally {
        try {
            if ($null -ne $previous) {
                Update-AuthenticationSeed -ChildHome $childHome -AuthenticationSeedPath $AuthenticationSeedPath
            }
        }
        finally {
            if ($null -ne $previous) {
                Restore-ChildHome -Previous $previous
            }
            Remove-ManagedDirectory -Path $childHome -ParentPath $childRoot
        }
    }
    $events = Read-Events -Path $eventPath
    $commands = Get-Commands -Events $events
    $commands | Set-Content -LiteralPath $commandsPath -Encoding UTF8
    $usage = Get-TokenUsage -Events $events
    $snapshotChanges = Get-SnapshotChanges -SnapshotPath $SnapshotPath -Baseline $SnapshotBaseline
    $issues = Get-ComplianceIssues -Arm $Arm -Commands $commands -Events $events -SnapshotChanges $snapshotChanges -ResolvedRoslynKitPath $ResolvedRoslynKitPath
    if (-not (Test-NonEmptyFile -Path $answerPath)) { $issues = @($issues + "no final answer was written") }
    $inputTokens = if ($null -ne $usage) { [long] $usage.input_tokens } else { $null }
    $cachedInputTokens = if ($null -ne $usage -and $null -ne $usage.cached_input_tokens) { [long] $usage.cached_input_tokens } else { $null }
    $uncachedInputTokens = if ($null -ne $inputTokens -and $null -ne $cachedInputTokens) { $inputTokens - $cachedInputTokens } else { $null }
    return [pscustomobject]@{
        timestamp_utc = [DateTime]::UtcNow.ToString("o"); case_id = $Case.id; arm = $Arm; trial = $Trial
        model = $Model; reasoning_effort = $ReasoningEffort; valid = ($exitCode -eq 0 -and $null -ne $inputTokens -and $issues.Count -eq 0)
        exit_code = $exitCode; duration_seconds = [Math]::Round(($completedAt - $startedAt).TotalSeconds, 3)
        input_tokens = $inputTokens; cached_input_tokens = $cachedInputTokens; uncached_input_tokens = $uncachedInputTokens
        command_count = $commands.Count; issues = ($issues -join " | "); answer_path = $answerPath
        events_path = $eventPath; stderr_path = $stderrPath; commands_path = $commandsPath
    }
}
function Get-Median {
    param([object[]] $Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }; $middle = [int]($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return [double] $sorted[$middle] }
    return (([double] $sorted[$middle - 1] + [double] $sorted[$middle]) / 2)
}
function Format-Metric {
    param($Value)
    if ($null -eq $Value) { return "" }; return ([double] $Value).ToString("0.##", [Globalization.CultureInfo]::InvariantCulture)
}
function Get-SavingsPercent {
    param($Raw, $RoslynKit)
    if ($null -eq $Raw -or $null -eq $RoslynKit -or $Raw -le 0) { return $null }; return 100.0 * ($Raw - $RoslynKit) / $Raw
}
function Write-Reports {
    param([string] $RunRoot, [object[]] $Rows, [object[]] $Cases)
    $Rows | Export-Csv -LiteralPath (Join-Path $RunRoot "runs.csv") -NoTypeInformation -Encoding UTF8
    $summary = @("# Codex Benchmark", "", "## By Case And Arm", "", "| Case | Arm | Valid runs | Median input | Median uncached input | Median duration (s) |", "| --- | --- | ---: | ---: | ---: | ---: |")
    $valid = @($Rows | Where-Object { $_.valid -eq $true -and $null -ne $_.input_tokens })
    foreach ($group in $Rows | Group-Object case_id, arm | Sort-Object Name) {
        $validRows = @($group.Group | Where-Object { $_.valid -and $null -ne $_.input_tokens })
        $input = Get-Median -Values @($validRows | ForEach-Object { $_.input_tokens })
        $uncached = Get-Median -Values @($validRows | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { $_.uncached_input_tokens })
        $duration = Get-Median -Values @($validRows | ForEach-Object { $_.duration_seconds })
        $parts = $group.Name -split ", "
        $summary += "| $($parts[0]) | $($parts[1]) | $($validRows.Count) | $(Format-Metric $input) | $(Format-Metric $uncached) | $(Format-Metric $duration) |"
    }
    $summary += @("", "## Raw-Codex Versus RoslynKit Savings", "", "| Case | Raw median input | RoslynKit median input | Input savings % | Raw median uncached | RoslynKit median uncached | Uncached savings % |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: |")
    foreach ($case in $Cases) {
        $raw = @($valid | Where-Object { $_.case_id -eq $case.id -and $_.arm -eq "raw-codex" })
        $roslynKit = @($valid | Where-Object { $_.case_id -eq $case.id -and $_.arm -eq "roslynkit" })
        $rawInput = Get-Median -Values @($raw | ForEach-Object { $_.input_tokens }); $roslynInput = Get-Median -Values @($roslynKit | ForEach-Object { $_.input_tokens })
        $rawUncached = Get-Median -Values @($raw | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { $_.uncached_input_tokens })
        $roslynUncached = Get-Median -Values @($roslynKit | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { $_.uncached_input_tokens })
        $summary += "| $($case.id) | $(Format-Metric $rawInput) | $(Format-Metric $roslynInput) | $(Format-Metric (Get-SavingsPercent $rawInput $roslynInput)) | $(Format-Metric $rawUncached) | $(Format-Metric $roslynUncached) | $(Format-Metric (Get-SavingsPercent $rawUncached $roslynUncached)) |"
    }
    $invalid = @($Rows | Where-Object { $_.valid -ne $true })
    $summary += @("", "## Invalid Runs", "")
    if ($invalid.Count -eq 0) { $summary += "None." }
    else {
        $summary += "| Case | Arm | Trial | Exit | Issues |", "| --- | --- | ---: | ---: | --- |"
        foreach ($row in $invalid) { $summary += "| $($row.case_id) | $($row.arm) | $($row.trial) | $($row.exit_code) | $($row.issues -replace '\|', '/') |" }
    }
    $summary | Set-Content -LiteralPath (Join-Path $RunRoot "summary.md") -Encoding UTF8
    $review = @("# Manual Review", "", "Review final answers against the private criteria below. These criteria are never included in child prompts.")
    foreach ($case in $Cases) {
        $review += ""
        $review += "## $($case.id)"
        $review += ""
        foreach ($criterion in $case.manualReviewCriteria) { $review += "- $criterion" }
        foreach ($row in $Rows | Where-Object { $_.case_id -eq $case.id }) { $review += "- $($row.arm) trial $($row.trial): $($row.answer_path) (valid: $($row.valid))" }
    }
    $review | Set-Content -LiteralPath (Join-Path $RunRoot "review.md") -Encoding UTF8
}
$repoRoot = Resolve-RepoRoot
$resolvedRoslynKitPath = Resolve-RoslynKitPath -Candidate $RoslynKitPath
$cases = Get-SelectedCases -Cases (Get-CaseData -RepoRoot $repoRoot) -SelectedCaseId $CaseId
if ($DryRun) {
    $placeholderSnapshot = "<clean-snapshot>"
    $disabledFeatures = Get-DisabledFeatures -DryRunMode
    foreach ($trial in 1..$Trials) {
        $arms = if (($trial % 2) -eq 1) { @("raw-codex", "roslynkit") } else { @("roslynkit", "raw-codex") }
        foreach ($case in $cases) {
            foreach ($arm in $arms) {
                $prompt = New-ArmPrompt -Arm $arm -Prompt $case.prompt -ResolvedRoslynKitPath $resolvedRoslynKitPath
                $arguments = New-CodexArguments -Prompt $prompt -SnapshotPath $placeholderSnapshot -AnswerPath "<artifacts-answer-path>" -DisabledFeatures $disabledFeatures
                Write-Host "[$($case.id)] $arm trial $trial"
                Write-Host ("codex " + (Format-CommandLine -Arguments $arguments))
                Write-Host "Prompt:"
                Write-Host $prompt
                Write-Host ""
            }
        }
    }
    return
}
if (-not (Test-RoslynKitPath -Path $resolvedRoslynKitPath)) {
    throw "RoslynKit was not found at '$resolvedRoslynKitPath'. Pass -RoslynKitPath."
}
if ($null -eq (Get-Command codex -ErrorAction SilentlyContinue)) {
    throw "The installed 'codex' executable is required."
}
$disabledFeatures = Get-DisabledFeatures
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $repoRoot "artifacts\codex-benchmark\$timestamp"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("roslynkit-codex-benchmark-" + [Guid]::NewGuid().ToString("N"))
foreach ($directory in @($runRoot, (Join-Path $runRoot "answers"), (Join-Path $runRoot "events"), (Join-Path $runRoot "stderr"), (Join-Path $runRoot "commands"), $temporaryRoot, (Join-Path $temporaryRoot "homes"))) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}
$rawSnapshot = $null
$roslynKitSnapshot = $null
try {
    $rawSnapshot = New-CleanSnapshot -RepoRoot $repoRoot -TemporaryRoot $temporaryRoot -SnapshotName "raw-snapshot"
    $roslynKitSnapshot = New-CleanSnapshot -RepoRoot $repoRoot -TemporaryRoot $temporaryRoot -SnapshotName "rsk-snapshot"
    Restore-SnapshotDependencies -SnapshotPath $rawSnapshot
    Restore-SnapshotDependencies -SnapshotPath $roslynKitSnapshot
    Initialize-RoslynKitIndex -SnapshotPath $roslynKitSnapshot -ResolvedRoslynKitPath $resolvedRoslynKitPath
    Stop-RoslynKitDaemon -SnapshotPath $roslynKitSnapshot -ResolvedRoslynKitPath $resolvedRoslynKitPath
    $rawSnapshotBaseline = Get-SnapshotState -SnapshotPath $rawSnapshot
    $roslynKitSnapshotBaseline = Get-SnapshotState -SnapshotPath $roslynKitSnapshot
    $authenticationSeedPath = New-AuthenticationSeed -TemporaryRoot $temporaryRoot -SourceCodexHome (Get-CodexHome)
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($trial in 1..$Trials) {
        $arms = if (($trial % 2) -eq 1) { @("raw-codex", "roslynkit") } else { @("roslynkit", "raw-codex") }
        foreach ($case in $cases) {
            foreach ($arm in $arms) {
                $snapshotPath = if ($arm -eq "raw-codex") { $rawSnapshot } else { $roslynKitSnapshot }
                $snapshotBaseline = if ($arm -eq "raw-codex") { $rawSnapshotBaseline } else { $roslynKitSnapshotBaseline }
                try {
                    $rows.Add((Invoke-BenchmarkRun -Case $case -Arm $arm -Trial $trial -SnapshotPath $snapshotPath -SnapshotBaseline $snapshotBaseline -RunRoot $runRoot -TemporaryRoot $temporaryRoot -AuthenticationSeedPath $authenticationSeedPath -ResolvedRoslynKitPath $resolvedRoslynKitPath -DisabledFeatures $disabledFeatures))
                    Write-Reports -RunRoot $runRoot -Rows $rows -Cases $cases
                }
                finally {
                    if ($arm -eq "roslynkit") {
                        Stop-RoslynKitDaemon -SnapshotPath $roslynKitSnapshot -ResolvedRoslynKitPath $resolvedRoslynKitPath
                    }
                }
            }
        }
    }
}
finally {
    try {
        Stop-RoslynKitDaemon -SnapshotPath $rawSnapshot -ResolvedRoslynKitPath $resolvedRoslynKitPath
        Stop-RoslynKitDaemon -SnapshotPath $roslynKitSnapshot -ResolvedRoslynKitPath $resolvedRoslynKitPath
        Remove-ManagedDirectory -Path (Join-Path $temporaryRoot "auth") -ParentPath $temporaryRoot
        Remove-ManagedDirectory -Path (Join-Path $temporaryRoot "homes") -ParentPath $temporaryRoot
    }
    finally {
        if (-not $KeepSnapshot) {
            Remove-ManagedDirectory -Path $temporaryRoot -ParentPath ([IO.Path]::GetTempPath())
        }
        else {
            if ($null -ne $rawSnapshot) { Write-Host "Retained raw snapshot: $rawSnapshot" }
            if ($null -ne $roslynKitSnapshot) { Write-Host "Retained RoslynKit snapshot: $roslynKitSnapshot" }
        }
    }
}
Write-Host "Benchmark complete: $runRoot"
