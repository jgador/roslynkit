[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Model = "gpt-5.6-luna",
    [ValidateNotNullOrEmpty()]
    [string] $ReasoningEffort = "high",
    [ValidateRange(1, 100)]
    [int] $Trials = 1,
    [string] $CaseId = "all",
    [switch] $DryRun,
    [switch] $KeepSnapshot
)
$ErrorActionPreference = "Stop"
[Environment]::SetEnvironmentVariable("CODEX_THREAD_ID", $null, "Process")
function Resolve-RepoRoot {
    $root = & git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) { throw "Run the benchmark from a Git worktree." }
    return (Resolve-Path -LiteralPath $root).Path
}
function Resolve-GlobalRoslynKitPath {
    $command = Get-Command roslynkit -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $(if ($command.Path) { $command.Path } else { $command.Source }) }
    return "roslynkit"
}
function Resolve-GlobalRipgrepPath {
    $command = Get-Command rg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $(if ($command.Path) { $command.Path } else { $command.Source }) }
    return "rg"
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
function New-ConditionPrompt {
    param([string] $Condition, [string] $Prompt, [string] $ResolvedRoslynKitPath)
    $rules = @(
        "Inspection-only benchmark condition: $Condition.",
        "Do not edit files or change Git state.",
        "Do not run builds, restores, tests, or other commands that write caches; inspect test source instead.",
        "Do not use web search, browsers, network requests, or subagents. Do not inspect memory, prior-session files, Atlas, CODEX_HOME, .codex, .agents, or AGENTS.md.",
        "Do not inspect benchmark scripts, benchmark data, or benchmark documentation.",
        "Use only simple inspection commands that do not modify the snapshot and are expected to succeed. A declined command or nonzero exit code invalidates the run.",
        "Return concise source-and-test evidence; do not change files."
    )
    if ($Condition -eq "raw-codex") {
        $rules += "Use ordinary local shell and text inspection only. Do not invoke RoslynKit, roslynkit-dev, or dotnet run for RoslynKit."
    }
    else {
        $rules += "Use this RoslynKit executable for code investigation: '$ResolvedRoslynKitPath'."
        $rules += "Pass --target .\RoslynKit.slnx to RoslynKit. The prepared snapshot-local search index is .\.benchmark\roslynkit.db; pass --index-path .\.benchmark\roslynkit.db to search."
    }
    return (($rules + "" + $Prompt) -join [Environment]::NewLine)
}
function New-CodexArguments {
    param([string] $Prompt, [string] $SnapshotPath, [string] $AnswerPath, [string[]] $DisabledFeatures)
    $arguments = @(
        "exec", "--config", 'approval_policy="never"', "--config", ('model_reasoning_effort="{0}"' -f $ReasoningEffort),
        "--config", "project_doc_max_bytes=0", "--config", "memories.use_memories=false", "--config", "memories.generate_memories=false",
        "--config", 'shell_environment_policy.inherit="core"',
        "--config", "shell_environment_policy.ignore_default_excludes=false", "--model", $Model, "--sandbox", "workspace-write",
        "--ephemeral", "--json", "--color", "never", "--cd", $SnapshotPath, "--output-last-message", $AnswerPath
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
function Get-SnapshotToolDirectory {
    param([string] $SnapshotPath)
    $benchmarkDirectory = Join-Path $SnapshotPath ".benchmark"
    $toolDirectory = Join-Path $benchmarkDirectory "tools"
    New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
    $excludePath = Join-Path $SnapshotPath ".git\info\exclude"
    if (-not (Select-String -LiteralPath $excludePath -SimpleMatch ".benchmark/" -Quiet -ErrorAction SilentlyContinue)) {
        Add-Content -LiteralPath $excludePath -Value ".benchmark/" -Encoding ascii
    }
    return $toolDirectory
}
function Install-SnapshotRipgrepTool {
    param([string] $SnapshotPath, [string] $ResolvedRipgrepPath)
    if (-not (Test-Path -LiteralPath $ResolvedRipgrepPath -PathType Leaf)) {
        throw "Ripgrep must resolve to a local executable before it can be staged in the benchmark snapshot: '$ResolvedRipgrepPath'."
    }
    $toolDirectory = Get-SnapshotToolDirectory -SnapshotPath $SnapshotPath
    $destinationPath = Join-Path $toolDirectory ([IO.Path]::GetFileName($ResolvedRipgrepPath))
    Copy-Item -LiteralPath $ResolvedRipgrepPath -Destination $destinationPath -Force
    return $destinationPath
}
function Install-SnapshotRoslynKitTool {
    param([string] $SnapshotPath, [string] $ResolvedRoslynKitPath)
    if (-not (Test-Path -LiteralPath $ResolvedRoslynKitPath -PathType Leaf)) {
        throw "RoslynKit must resolve to a local executable before it can be staged in the benchmark snapshot: '$ResolvedRoslynKitPath'."
    }
    $toolDirectory = Get-SnapshotToolDirectory -SnapshotPath $SnapshotPath
    $sourceDirectory = Split-Path -Parent $ResolvedRoslynKitPath
    $destinationPath = Join-Path $toolDirectory ([IO.Path]::GetFileName($ResolvedRoslynKitPath))
    Copy-Item -LiteralPath $ResolvedRoslynKitPath -Destination $destinationPath -Force
    $globalToolStore = Join-Path $sourceDirectory ".store\roslynkit"
    if (Test-Path -LiteralPath $globalToolStore -PathType Container) {
        $destinationStore = Join-Path $toolDirectory ".store"
        New-Item -ItemType Directory -Force -Path $destinationStore | Out-Null
        Copy-Item -LiteralPath $globalToolStore -Destination $destinationStore -Recurse -Force
    }
    else {
        $toolStem = [IO.Path]::GetFileNameWithoutExtension($ResolvedRoslynKitPath)
        $hasAdjacentPayload = (Test-Path -LiteralPath (Join-Path $sourceDirectory "$toolStem.dll") -PathType Leaf) -or
            (Test-Path -LiteralPath (Join-Path $sourceDirectory "$toolStem.deps.json") -PathType Leaf)
        if ($hasAdjacentPayload) {
            foreach ($entry in Get-ChildItem -LiteralPath $sourceDirectory -Force) {
                Copy-Item -LiteralPath $entry.FullName -Destination $toolDirectory -Recurse -Force
            }
        }
    }
    Push-Location $SnapshotPath
    try {
        $versionOutput = @(& $destinationPath --version 2>&1)
        if ($LASTEXITCODE -ne 0 -or ($versionOutput -join "`n") -notmatch "(?i)roslynkit version") {
            throw "The staged RoslynKit executable failed its direct version check: $($versionOutput -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
    return $destinationPath
}
function Initialize-RoslynKitIndex {
    param([string] $SnapshotPath, [string] $ResolvedRoslynKitPath)
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
function Set-WorkstationCodexHome {
    $codexHome = if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        $env:CODEX_HOME
    }
    else {
        Join-Path $env:USERPROFILE ".codex"
    }
    if (-not (Test-Path -LiteralPath $codexHome -PathType Container)) {
        throw "The active workstation CODEX_HOME directory was not found: '$codexHome'."
    }
    $resolvedHome = (Resolve-Path -LiteralPath $codexHome).Path
    $configPath = Join-Path $resolvedHome "config.toml"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "The active workstation Codex configuration was not found: '$configPath'."
    }
    [Environment]::SetEnvironmentVariable("CODEX_HOME", $resolvedHome, "Process")
    return $configPath
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
    return $events.ToArray()
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
    $Command = $Command.Replace("\\", "\")
    $path = [regex]::Escape($ResolvedRoslynKitPath.Replace("/", "\\"))
    $file = [regex]::Escape([IO.Path]::GetFileName($ResolvedRoslynKitPath))
    $prefix = '(?i)(^|[;&|]\s*)(?:&\s*)?'
    $quote = '["'']?'
    $resolvedPathPattern = '(?i)(?:&\s*)?' + $quote + $path + $quote + '\s+\S+'
    $namedPattern = $prefix + '(?:' + $quote + $file + $quote + '|roslynkit(?:-dev)?(?:\.exe)?)\s+\S+'
    $dotnetPattern = '(?i)(^|[;&|]\s*)dotnet(?:\.exe)?\s+run\b(?=[^\r\n]*--project\s+' + $quote + '[^;\r\n]*src[\\/]RoslynKit(?:[\\/]|["'']|\s))'
    return $Command -match $resolvedPathPattern -or $Command -match $namedPattern -or $Command -match $dotnetPattern
}
function Test-ForbiddenContextSurface {
    param([string] $Command)
    $commandWithoutNegativeGlobs = [regex]::Replace($Command, '![^\s]+', '')
    $commandWithoutExclusionFilters = [regex]::Replace($commandWithoutNegativeGlobs, '(?is)-notin\s+@\([^)]*\)', '')
    return $commandWithoutExclusionFilters -match "(?i)(CODEX_HOME|\.codex|\.agents|AGENTS\.md|MEMORY\.md|rollout-|history\.jsonl|atlas-(csharp|doc|test)-mapper|codex-benchmark|benchmark-codex|codex-cases\.json|token-efficiency-benchmark)"
}
function Get-ComplianceIssues {
    param([string] $Condition, [string[]] $Commands, [object[]] $Events, [string[]] $SnapshotChanges, [string] $ResolvedRoslynKitPath)
    $issues = New-Object System.Collections.Generic.List[string]
    $usedRoslynKit = @($Events | Where-Object {
            $_.item.type -eq "command_execution" -and $_.item.status -eq "completed" -and $_.item.exit_code -eq 0 -and
            (Test-RoslynKitInvocation -Command ([string] $_.item.command) -ResolvedRoslynKitPath $ResolvedRoslynKitPath)
        }).Count -gt 0
    foreach ($command in $Commands) {
        $usesRoslynKit = Test-RoslynKitInvocation -Command $command -ResolvedRoslynKitPath $ResolvedRoslynKitPath
        if ($Condition -eq "raw-codex" -and $usesRoslynKit) {
            $issues.Add("raw-codex invoked RoslynKit: $command")
        }
        if ($command -match "(?i)\b(curl|wget|Invoke-WebRequest|Invoke-RestMethod)\b|https?://") {
            $issues.Add("used web or network access: $command")
        }
        if (Test-ForbiddenContextSurface -Command $command) {
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
        if ($event.type -eq "item.completed" -and $event.item.type -eq "command_execution" -and
            ($event.item.status -ne "completed" -or $event.item.exit_code -ne 0)) {
            $command = ([string] $event.item.command -replace '\s+', ' ').Trim()
            if ($command.Length -gt 240) { $command = $command.Substring(0, 237) + "..." }
            $issues.Add("command failed (status=$($event.item.status), exit=$($event.item.exit_code)): $command")
        }
    }
    if ($Condition -eq "roslynkit" -and -not $usedRoslynKit) {
        $issues.Add("RoslynKit condition did not invoke RoslynKit")
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
    return @(Compare-Object -ReferenceObject $Baseline -DifferenceObject (Get-SnapshotState -SnapshotPath $SnapshotPath) |
        ForEach-Object { $_.InputObject } |
        Where-Object { $_ -notmatch '^!! \.benchmark/roslynkit\.db-(shm|wal)$' })
}
function Invoke-BenchmarkRun {
    param([object] $Case, [string] $Condition, [int] $Trial, [string] $SnapshotPath, [string[]] $SnapshotBaseline, [string] $RunRoot, [string] $ResolvedRoslynKitPath, [string[]] $DisabledFeatures)
    $runId = "{0}-{1}-trial{2}" -f $Case.id, $Condition, $Trial
    $answerPath = Join-Path $RunRoot "answers\$runId.md"
    $eventPath = Join-Path $RunRoot "events\$runId.jsonl"
    $stderrPath = Join-Path $RunRoot "stderr\$runId.txt"
    $commandsPath = Join-Path $RunRoot "commands\$runId.txt"
    $prompt = New-ConditionPrompt -Condition $Condition -Prompt $Case.prompt -ResolvedRoslynKitPath $ResolvedRoslynKitPath
    $arguments = New-CodexArguments -Prompt $prompt -SnapshotPath $SnapshotPath -AnswerPath $answerPath -DisabledFeatures $DisabledFeatures
    $exitCode = -1
    $startedAt = [DateTime]::UtcNow
    $completedAt = $startedAt
    if ((Get-SnapshotChanges -SnapshotPath $SnapshotPath -Baseline $SnapshotBaseline).Count -gt 0) {
        throw "The prepared snapshot is dirty before '$runId'."
    }
    try {
        $startedAt = [DateTime]::UtcNow
        $oldPreference = $ErrorActionPreference
        $oldPath = $env:PATH
        $ErrorActionPreference = "Continue"
        $env:PATH = (Join-Path $SnapshotPath ".benchmark\tools") + [IO.Path]::PathSeparator + $oldPath
        Push-Location $SnapshotPath
        try {
            & codex @arguments 1> $eventPath 2> $stderrPath
            $completedAt = [DateTime]::UtcNow
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
            $env:PATH = $oldPath
            $ErrorActionPreference = $oldPreference
        }
    }
    catch {
        $completedAt = [DateTime]::UtcNow
        $_ | Out-String | Set-Content -LiteralPath $stderrPath -Encoding UTF8
    }
    $events = Read-Events -Path $eventPath
    $commands = Get-Commands -Events $events
    $commands | Set-Content -LiteralPath $commandsPath -Encoding UTF8
    $usage = Get-TokenUsage -Events $events
    $snapshotChanges = Get-SnapshotChanges -SnapshotPath $SnapshotPath -Baseline $SnapshotBaseline
    $issues = Get-ComplianceIssues -Condition $Condition -Commands $commands -Events $events -SnapshotChanges $snapshotChanges -ResolvedRoslynKitPath $ResolvedRoslynKitPath
    if (-not (Test-NonEmptyFile -Path $answerPath)) { $issues = @($issues + "no final answer was written") }
    $inputTokens = if ($null -ne $usage) { [long] $usage.input_tokens } else { $null }
    $cachedInputTokens = if ($null -ne $usage -and $null -ne $usage.cached_input_tokens) { [long] $usage.cached_input_tokens } else { $null }
    $uncachedInputTokens = if ($null -ne $inputTokens -and $null -ne $cachedInputTokens) { $inputTokens - $cachedInputTokens } else { $null }
    return [pscustomobject]@{
        timestamp_utc = [DateTime]::UtcNow.ToString("o"); case_id = $Case.id; condition = $Condition; trial = $Trial
        model = $Model; reasoning_effort = $ReasoningEffort; valid = ($exitCode -eq 0 -and $null -ne $inputTokens -and $issues.Count -eq 0)
        exit_code = $exitCode; duration_seconds = [Math]::Round(($completedAt - $startedAt).TotalSeconds, 3)
        input_tokens = $inputTokens; cached_input_tokens = $cachedInputTokens; uncached_input_tokens = $uncachedInputTokens
        command_count = $commands.Count; issues = ($issues -join " | "); answer_path = $answerPath
        events_path = $eventPath; stderr_path = $stderrPath; commands_path = $commandsPath
    }
}
function Invoke-BenchmarkPreflight {
    param([string] $SnapshotPath, [string] $RunRoot, [string] $ResolvedRoslynKitPath, [string[]] $DisabledFeatures)
    $preflightRoot = Join-Path $RunRoot "preflight"
    New-Item -ItemType Directory -Force -Path $preflightRoot | Out-Null
    $answerPath = Join-Path $preflightRoot "answer.md"
    $eventPath = Join-Path $preflightRoot "events.jsonl"
    $stderrPath = Join-Path $preflightRoot "stderr.txt"
    $commandsPath = Join-Path $preflightRoot "commands.txt"
    $escapedPath = $ResolvedRoslynKitPath.Replace("'", "''")
    $prompt = "Run exactly this command once: `$rgPath = (Get-Command rg -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source; & `$rgPath --version; if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }; & '$escapedPath' --version. Then reply with exactly both version outputs and nothing else."
    $arguments = New-CodexArguments -Prompt $prompt -SnapshotPath $SnapshotPath -AnswerPath $answerPath -DisabledFeatures $DisabledFeatures
    $exitCode = -1
    $oldPreference = $ErrorActionPreference
    $oldPath = $env:PATH
    $ErrorActionPreference = "Continue"
    $env:PATH = (Join-Path $SnapshotPath ".benchmark\tools") + [IO.Path]::PathSeparator + $oldPath
    try {
        Push-Location $SnapshotPath
        try {
            & codex @arguments 1> $eventPath 2> $stderrPath
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
    }
    finally {
        $env:PATH = $oldPath
        $ErrorActionPreference = $oldPreference
    }
    $events = Read-Events -Path $eventPath
    $commands = Get-Commands -Events $events
    $commands | Set-Content -LiteralPath $commandsPath -Encoding UTF8
    $completedCommands = @($events | Where-Object { $_.type -eq "item.completed" -and $_.item.type -eq "command_execution" })
    $failedCommands = @($completedCommands | Where-Object { $_.item.status -ne "completed" -or $_.item.exit_code -ne 0 })
    $successfulRoslynKitCommands = @($completedCommands | Where-Object {
            $_.item.status -eq "completed" -and $_.item.exit_code -eq 0 -and
            (Test-RoslynKitInvocation -Command ([string] $_.item.command) -ResolvedRoslynKitPath $ResolvedRoslynKitPath)
        })
    $successfulRipgrepCommands = @($completedCommands | Where-Object {
            $_.item.status -eq "completed" -and $_.item.exit_code -eq 0 -and
            [string] $_.item.command -match '(?i)\bGet-Command\s+rg\b'
        })
    $answer = if (Test-Path -LiteralPath $answerPath -PathType Leaf) { Get-Content -Raw -LiteralPath $answerPath } else { "" }
    if ($exitCode -ne 0 -or $completedCommands.Count -ne 1 -or $failedCommands.Count -gt 0 -or $successfulRoslynKitCommands.Count -ne 1 -or
        $successfulRipgrepCommands.Count -ne 1 -or $answer -notmatch "(?im)^ripgrep\s+\d" -or $answer -notmatch "(?i)roslynkit version") {
        throw "Benchmark preflight failed before measured sessions. Inspect '$preflightRoot'."
    }
    Write-Host "Benchmark preflight passed: $preflightRoot"
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
    $summary = @("# Codex Benchmark", "", "## By Case And Condition", "", "| Case | Condition | Valid runs | Median input | Median uncached input | Median duration (s) |", "| --- | --- | ---: | ---: | ---: | ---: |")
    $valid = @($Rows | Where-Object { $_.valid -eq $true -and $null -ne $_.input_tokens })
    foreach ($group in $Rows | Group-Object case_id, condition | Sort-Object Name) {
        $validRows = @($group.Group | Where-Object { $_.valid -and $null -ne $_.input_tokens })
        $input = Get-Median -Values @($validRows | ForEach-Object { $_.input_tokens })
        $uncached = Get-Median -Values @($validRows | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { $_.uncached_input_tokens })
        $duration = Get-Median -Values @($validRows | ForEach-Object { $_.duration_seconds })
        $parts = $group.Name -split ", "
        $summary += "| $($parts[0]) | $($parts[1]) | $($validRows.Count) | $(Format-Metric $input) | $(Format-Metric $uncached) | $(Format-Metric $duration) |"
    }
    $summary += @("", "## Raw-Codex Versus RoslynKit Savings", "", "| Case | Raw median input | RoslynKit median input | Input savings % | Raw median uncached | RoslynKit median uncached | Uncached savings % |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: |")
    foreach ($case in $Cases) {
        $raw = @($valid | Where-Object { $_.case_id -eq $case.id -and $_.condition -eq "raw-codex" })
        $roslynKit = @($valid | Where-Object { $_.case_id -eq $case.id -and $_.condition -eq "roslynkit" })
        $rawInput = Get-Median -Values @($raw | ForEach-Object { $_.input_tokens }); $roslynInput = Get-Median -Values @($roslynKit | ForEach-Object { $_.input_tokens })
        $rawUncached = Get-Median -Values @($raw | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { $_.uncached_input_tokens })
        $roslynUncached = Get-Median -Values @($roslynKit | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { $_.uncached_input_tokens })
        $summary += "| $($case.id) | $(Format-Metric $rawInput) | $(Format-Metric $roslynInput) | $(Format-Metric (Get-SavingsPercent $rawInput $roslynInput)) | $(Format-Metric $rawUncached) | $(Format-Metric $roslynUncached) | $(Format-Metric (Get-SavingsPercent $rawUncached $roslynUncached)) |"
    }
    $invalid = @($Rows | Where-Object { $_.valid -ne $true })
    $summary += @("", "## Invalid Runs", "")
    if ($invalid.Count -eq 0) { $summary += "None." }
    else {
        $summary += "| Case | Condition | Trial | Exit | Issues |", "| --- | --- | ---: | ---: | --- |"
        foreach ($row in $invalid) { $summary += "| $($row.case_id) | $($row.condition) | $($row.trial) | $($row.exit_code) | $($row.issues -replace '\|', '/') |" }
    }
    $summary | Set-Content -LiteralPath (Join-Path $RunRoot "summary.md") -Encoding UTF8
    $review = @("# Manual Review", "", "Review final answers against the private criteria below. These criteria are never included in child prompts.")
    foreach ($case in $Cases) {
        $review += ""
        $review += "## $($case.id)"
        $review += ""
        foreach ($criterion in $case.manualReviewCriteria) { $review += "- $criterion" }
        foreach ($row in $Rows | Where-Object { $_.case_id -eq $case.id }) { $review += "- $($row.condition) trial $($row.trial): $($row.answer_path) (valid: $($row.valid))" }
    }
    $review | Set-Content -LiteralPath (Join-Path $RunRoot "review.md") -Encoding UTF8
}
$repoRoot = Resolve-RepoRoot
$resolvedRoslynKitPath = Resolve-GlobalRoslynKitPath
$resolvedRipgrepPath = Resolve-GlobalRipgrepPath
$cases = Get-SelectedCases -Cases (Get-CaseData -RepoRoot $repoRoot) -SelectedCaseId $CaseId
$activeCodexConfigPath = Set-WorkstationCodexHome
if ($DryRun) {
    $placeholderSnapshot = "<clean-snapshot>"
    $placeholderRoslynKitPath = ".\.benchmark\tools\roslynkit.exe"
    $disabledFeatures = Get-DisabledFeatures -DryRunMode
    Write-Host "Active Codex config: $activeCodexConfigPath"
    Write-Host "Environment: the workstation CODEX_HOME is used directly; benchmark-specific command-line overrides remain in effect."
    Write-Host "RoslynKit condition: the global tool and its package payload are staged inside the isolated snapshot; child sessions use the workspace-write sandbox, the runner installs no command-policy rules, and workstation rules remain active."
    Write-Host "Preflight: one isolated, unmeasured command must resolve snapshot-local ripgrep and run both ripgrep and RoslynKit version checks before any measured session starts."
    Write-Host "Validity: any declined or nonzero command invalidates the session and stops the benchmark before the next session."
    Write-Host ""
    foreach ($trial in 1..$Trials) {
        $conditions = if (($trial % 2) -eq 1) { @("raw-codex", "roslynkit") } else { @("roslynkit", "raw-codex") }
        foreach ($case in $cases) {
            foreach ($condition in $conditions) {
                $conditionRoslynKitPath = if ($condition -eq "roslynkit") { $placeholderRoslynKitPath } else { $resolvedRoslynKitPath }
                $prompt = New-ConditionPrompt -Condition $condition -Prompt $case.prompt -ResolvedRoslynKitPath $conditionRoslynKitPath
                $arguments = New-CodexArguments -Prompt $prompt -SnapshotPath $placeholderSnapshot -AnswerPath "<artifacts-answer-path>" -DisabledFeatures $disabledFeatures
                Write-Host "[$($case.id)] $condition trial $trial"
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
    throw "The global 'roslynkit' tool was not found on PATH. Install the global tool before running the benchmark."
}
if ($null -eq (Get-Command codex -ErrorAction SilentlyContinue)) {
    throw "The installed 'codex' executable is required."
}
if (-not (Test-Path -LiteralPath $resolvedRipgrepPath -PathType Leaf)) {
    throw "The global 'rg' application was not found on PATH. Install ripgrep before running the benchmark."
}
$disabledFeatures = Get-DisabledFeatures
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $repoRoot "artifacts\codex-benchmark\$timestamp"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("roslynkit-codex-benchmark-" + [Guid]::NewGuid().ToString("N"))
foreach ($directory in @($runRoot, (Join-Path $runRoot "answers"), (Join-Path $runRoot "events"), (Join-Path $runRoot "stderr"), (Join-Path $runRoot "commands"), $temporaryRoot)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}
$rawSnapshot = $null
$roslynKitSnapshot = $null
$snapshotRoslynKitPath = $resolvedRoslynKitPath
try {
    $rawSnapshot = New-CleanSnapshot -RepoRoot $repoRoot -TemporaryRoot $temporaryRoot -SnapshotName "raw-snapshot"
    $roslynKitSnapshot = New-CleanSnapshot -RepoRoot $repoRoot -TemporaryRoot $temporaryRoot -SnapshotName "rsk-snapshot"
    Restore-SnapshotDependencies -SnapshotPath $rawSnapshot
    Restore-SnapshotDependencies -SnapshotPath $roslynKitSnapshot
    Install-SnapshotRipgrepTool -SnapshotPath $rawSnapshot -ResolvedRipgrepPath $resolvedRipgrepPath | Out-Null
    Install-SnapshotRipgrepTool -SnapshotPath $roslynKitSnapshot -ResolvedRipgrepPath $resolvedRipgrepPath | Out-Null
    $snapshotRoslynKitPath = Install-SnapshotRoslynKitTool -SnapshotPath $roslynKitSnapshot -ResolvedRoslynKitPath $resolvedRoslynKitPath
    Initialize-RoslynKitIndex -SnapshotPath $roslynKitSnapshot -ResolvedRoslynKitPath $snapshotRoslynKitPath
    Stop-RoslynKitDaemon -SnapshotPath $roslynKitSnapshot -ResolvedRoslynKitPath $snapshotRoslynKitPath
    $rawSnapshotBaseline = Get-SnapshotState -SnapshotPath $rawSnapshot
    $roslynKitSnapshotBaseline = Get-SnapshotState -SnapshotPath $roslynKitSnapshot
    Invoke-BenchmarkPreflight -SnapshotPath $roslynKitSnapshot -RunRoot $runRoot -ResolvedRoslynKitPath $snapshotRoslynKitPath -DisabledFeatures $disabledFeatures
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($trial in 1..$Trials) {
        $conditions = if (($trial % 2) -eq 1) { @("raw-codex", "roslynkit") } else { @("roslynkit", "raw-codex") }
        foreach ($case in $cases) {
            foreach ($condition in $conditions) {
                $snapshotPath = if ($condition -eq "raw-codex") { $rawSnapshot } else { $roslynKitSnapshot }
                $snapshotBaseline = if ($condition -eq "raw-codex") { $rawSnapshotBaseline } else { $roslynKitSnapshotBaseline }
                $conditionRoslynKitPath = if ($condition -eq "raw-codex") { $resolvedRoslynKitPath } else { $snapshotRoslynKitPath }
                try {
                    $row = Invoke-BenchmarkRun -Case $case -Condition $condition -Trial $trial -SnapshotPath $snapshotPath -SnapshotBaseline $snapshotBaseline -RunRoot $runRoot -ResolvedRoslynKitPath $conditionRoslynKitPath -DisabledFeatures $disabledFeatures
                    $rows.Add($row)
                    Write-Reports -RunRoot $runRoot -Rows $rows -Cases $cases
                    if (-not $row.valid) {
                        throw "Benchmark stopped after invalid session '$($row.case_id)/$($row.condition)/trial$($row.trial)': $($row.issues)"
                    }
                }
                finally {
                    if ($condition -eq "roslynkit") {
                        Stop-RoslynKitDaemon -SnapshotPath $roslynKitSnapshot -ResolvedRoslynKitPath $snapshotRoslynKitPath
                    }
                }
            }
        }
    }
}
finally {
    try {
        Stop-RoslynKitDaemon -SnapshotPath $rawSnapshot -ResolvedRoslynKitPath $resolvedRoslynKitPath
        Stop-RoslynKitDaemon -SnapshotPath $roslynKitSnapshot -ResolvedRoslynKitPath $snapshotRoslynKitPath
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
