[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Model = "gpt-5.6-luna",
    [ValidateNotNullOrEmpty()]
    [string] $ReasoningEffort = "high",
    [ValidateRange(1, 100)]
    [int] $Trials = 1,
    [string] $CaseId = "all",
    [switch] $DryRun
)
$ErrorActionPreference = "Stop"
$RoslynKitShellTimeoutMilliseconds = 120000
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
    param([string] $Condition, [string] $Prompt)
    $rules = @(
        "Inspection-only benchmark condition: $Condition.",
        "As the first command, read .agents/skills/benchmark/SKILL.md with Get-Content -Raw before investigating code.",
        "Do not edit files or change Git state.",
        "Do not run builds, restores, tests, or other commands that write caches; inspect test source instead.",
        "Do not use web search, browsers, network requests, or subagents. Do not inspect memory, prior-session files, Atlas, CODEX_HOME, .codex, AGENTS.md, or agent context not explicitly permitted here.",
        "Do not inspect the benchmark controller, private benchmark data, prior benchmark artifacts, or benchmark procedure documentation.",
        "Use only simple inspection commands that do not modify the repository and are expected to succeed. A declined command or nonzero exit code invalidates the run.",
        "Return concise source-and-test evidence; do not change files."
    )
    if ($Condition -eq "raw-codex") {
        $rules += "Do not read .agents/skills/roslynkit or any file below it."
        $rules += "Use ordinary local shell and text inspection only. Do not invoke RoslynKit, roslynkit-dev, or dotnet run for RoslynKit."
    }
    else {
        $rules += "Then read .agents/skills/roslynkit/SKILL.md, .agents/skills/roslynkit/references/commands.md, and .agents/skills/roslynkit/references/output.md with Get-Content -Raw before invoking RoslynKit."
        $rules += "Invoke the global RoslynKit from PATH as 'roslynkit' for code investigation."
        $rules += "Pass --target ./RoslynKit.slnx to RoslynKit. The prepared repository-local search index is ./artifacts/roslynkit.db; pass --index-path ./artifacts/roslynkit.db to search."
        $rules += "Set timeout_ms to $RoslynKitShellTimeoutMilliseconds on every shell tool call that invokes RoslynKit; the shell tool's default deadline is too short for a cold workspace command."
        $rules += "Run only one RoslynKit command at a time and wait for it to finish before starting another. Do not use concurrent tool calls, background jobs, or parallel pipelines for RoslynKit."
        $rules += "Start intent discovery with one narrow roslynkit search query and --max-results 5. Refine serially only when the first result set is insufficient, and prefer bounded source slices over whole-file output."
        $rules += "Treat every id: selector as opaque and copy it verbatim. When an id contains PowerShell backticks, either pass it as one single-quoted --symbol value or use its returned loc with a bounded document-lines call; never reconstruct or rewrite the id."
    }
    return (($rules + "" + $Prompt) -join [Environment]::NewLine)
}
function New-CodexArguments {
    param([string] $Prompt, [string] $RepoRoot, [string] $AnswerPath, [string[]] $DisabledFeatures)
    $arguments = @(
        "exec", "--dangerously-bypass-approvals-and-sandbox", "--config", ('model_reasoning_effort="{0}"' -f $ReasoningEffort),
        "--config", "project_doc_max_bytes=0", "--config", "memories.use_memories=false", "--config", "memories.generate_memories=false",
        "--config", 'shell_environment_policy.inherit="all"', "--model", $Model,
        "--ephemeral", "--json", "--color", "never", "--cd", $RepoRoot, "--output-last-message", $AnswerPath
    )
    foreach ($feature in $DisabledFeatures) { $arguments += "--disable", $feature }
    $arguments += $Prompt; return $arguments
}
function Get-DisabledFeatures {
    param([switch] $DryRunMode)
    $requested = @("apps", "browser_use", "browser_use_external", "browser_use_full_cdp_access", "computer_use", "external_agent_memory_import", "goals", "hooks", "image_generation", "in_app_browser", "memories", "multi_agent", "multi_agent_v2", "plugin_sharing", "plugins", "remote_plugin", "shell_snapshot", "skill_mcp_dependency_install", "skill_search", "standalone_web_search", "workspace_dependencies")
    if ($DryRunMode) { return $requested }
    $featureLines = @(& codex features list)
    if ($LASTEXITCODE -ne 0) { throw "The installed Codex CLI could not enumerate features for benchmark isolation." }
    $available = @($featureLines | ForEach-Object { ($_ -split '\s+')[0] })
    return @($requested | Where-Object { $available -contains $_ })
}
function Format-CommandLine {
    param([string[]] $Arguments)
    return (($Arguments | ForEach-Object {
                if ($_ -match '^[A-Za-z0-9_./:\\=-]+$') { $_ } else { "'" + $_.Replace("'", "''") + "'" }
            }) -join " ")
}
function Restore-RepositoryDependencies {
    param([string] $RepoRoot)
    $solution = Join-Path $RepoRoot "RoslynKit.slnx"
    & dotnet restore $solution --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Repository restore failed before measured runs."
    }
}
function Initialize-RoslynKitIndex {
    param([string] $RepoRoot, [string] $ResolvedRoslynKitPath)
    Push-Location $RepoRoot
    try {
        New-Item -ItemType Directory -Force -Path "./artifacts" | Out-Null
        & $ResolvedRoslynKitPath index --target "./RoslynKit.slnx" --index-path "./artifacts/roslynkit.db"
        if ($LASTEXITCODE -ne 0) {
            throw "RoslynKit index preparation failed before measured runs."
        }
    }
    finally {
        Pop-Location
    }
}
function Stop-RoslynKitDaemon {
    param([string] $RepoRoot, [string] $ResolvedRoslynKitPath)
    if ([string]::IsNullOrWhiteSpace($RepoRoot) -or -not (Test-Path -LiteralPath (Join-Path $RepoRoot "RoslynKit.slnx") -PathType Leaf)) {
        return
    }
    Push-Location $RepoRoot
    try {
        $stopOutput = @(& $ResolvedRoslynKitPath daemon stop --target "./RoslynKit.slnx" 2>&1)
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "RoslynKit daemon cleanup failed for '$RepoRoot': $($stopOutput -join ' ')"
            return
        }
        if (($stopOutput -join "`n") -match "state: not-running") {
            return
        }
        foreach ($attempt in 1..20) {
            $statusOutput = @(& $ResolvedRoslynKitPath daemon status --target "./RoslynKit.slnx" 2>&1)
            if ($LASTEXITCODE -eq 0 -and ($statusOutput -join "`n") -match "state: not-running") {
                return
            }
            Start-Sleep -Milliseconds 250
        }
        Write-Warning "RoslynKit daemon did not stop within five seconds for '$RepoRoot'."
    }
    catch {
        Write-Warning "RoslynKit daemon cleanup failed for '$RepoRoot': $($_.Exception.Message)"
    }
    finally {
        Pop-Location
    }
}
function Get-DefaultCodexHome {
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ([string]::IsNullOrWhiteSpace($userProfile)) {
        throw "Could not resolve a user-profile directory for the default CODEX_HOME. Set CODEX_HOME explicitly."
    }
    return Join-Path $userProfile ".codex"
}
function Set-WorkstationCodexHome {
    $codexHome = if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        $env:CODEX_HOME
    }
    else {
        Get-DefaultCodexHome
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
    param([string] $Command, [string] $ResolvedRoslynKitPath, [int] $Depth = 0)
    if ([string]::IsNullOrWhiteSpace($Command) -or $Depth -gt 4) { return $false }
    $trimmedCommand = $Command.Trim()
    $parseInput = if ($trimmedCommand -match '^["'']') { "& $trimmedCommand" } else { $trimmedCommand }
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($parseInput, [ref] $tokens, [ref] $parseErrors)
    $pathComparison = if ($IsWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
    $resolvedPath = $ResolvedRoslynKitPath.Trim('"', "'")
    try {
        $resolvedFile = [IO.Path]::GetFileName($resolvedPath)
    }
    catch {
        $resolvedFile = ""
    }
    $knownFiles = @("roslynkit", "roslynkit.exe", "roslynkit-dev", "roslynkit-dev.exe", $resolvedFile)
    $commandAsts = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst]
            }, $true))
    foreach ($commandAst in $commandAsts) {
        $commandName = $commandAst.GetCommandName()
        if ([string]::IsNullOrWhiteSpace($commandName)) { continue }
        $normalizedName = $commandName.Trim('"', "'")
        try {
            $commandFile = [IO.Path]::GetFileName($normalizedName)
        }
        catch {
            $commandFile = ""
        }
        if ([string]::Equals($normalizedName, $resolvedPath, $pathComparison) -or
            @($knownFiles | Where-Object { [string]::Equals($_, $commandFile, $pathComparison) }).Count -gt 0) {
            return $true
        }
        if ($commandFile -in @("pwsh", "pwsh.exe", "powershell", "powershell.exe")) {
            $payloadMatch = [regex]::Match($commandAst.Extent.Text, '(?is)\s-(?:Command|c)\s+(?<payload>.+)$')
            if ($payloadMatch.Success) {
                $payload = $payloadMatch.Groups["payload"].Value.Trim()
                if ($payload.Length -ge 2 -and (($payload[0] -eq '"' -and $payload[$payload.Length - 1] -eq '"') -or
                        ($payload[0] -eq "'" -and $payload[$payload.Length - 1] -eq "'"))) {
                    $payload = $payload.Substring(1, $payload.Length - 2)
                }
                if (Test-RoslynKitInvocation -Command $payload -ResolvedRoslynKitPath $ResolvedRoslynKitPath -Depth ($Depth + 1)) {
                    return $true
                }
            }
        }
    }
    $prefix = '(?i)(^|[;&|]\s*)(?:&\s*)?'
    $quote = '["'']?'
    $dotnetPattern = '(?i)(^|[;&|]\s*)dotnet(?:\.exe)?\s+run\b(?=[^\r\n]*--project\s+' + $quote + '[^;\r\n]*src[\\/]RoslynKit(?:[\\/]|["'']|\s))'
    $resolverMatch = [regex]::Match($Command, '(?i)\$(?<variable>[a-z_][a-z0-9_]*)\s*=\s*\(\s*Get-Command\s+roslynkit(?:\.exe)?\b[^)]*\)')
    $resolvedVariablePattern = if ($resolverMatch.Success) {
        $prefix + '\$' + [regex]::Escape($resolverMatch.Groups['variable'].Value) + '\s+(?!=)\S+'
    }
    else {
        '(?!)'
    }
    $directResolverInvocationPattern = '(?is)&\s*(?:\(\s*Get-Command\s+roslynkit(?:\.exe)?\b[^)]*\)(?:\.(?:Path|Source))?|\$\(\s*Get-Command\s+roslynkit(?:\.exe)?\b[^)]*\))\s+\S+'
    $indirectShellPattern = '(?is)\b(?:cmd(?:\.exe)?\s+/(?:c|k)|Invoke-Expression|iex)\b[^\r\n;]*["'']?(?:[^"''\s]*[\\/])?roslynkit(?:-dev)?(?:\.exe)?(?:["'']|\s|$)'
    $processStartPattern = '(?is)::Start\(\s*["''](?:[^"'']*[\\/])?roslynkit(?:-dev)?(?:\.exe)?["'']'
    return $Command -match $resolvedVariablePattern -or
        $Command -match $dotnetPattern -or
        $Command -match $directResolverInvocationPattern -or
        $Command -match $indirectShellPattern -or
        $Command -match $processStartPattern
}
function Test-ConcurrentRoslynKitInvocations {
    param([object[]] $Events, [string] $ResolvedRoslynKitPath)
    $activeCommands = New-Object System.Collections.Generic.HashSet[string]
    foreach ($event in $Events) {
        if ($event.type -notin @("item.started", "item.completed") -or $event.item.type -ne "command_execution" -or
            -not (Test-RoslynKitInvocation -Command ([string] $event.item.command) -ResolvedRoslynKitPath $ResolvedRoslynKitPath)) {
            continue
        }
        $itemId = [string] $event.item.id
        if ($event.type -eq "item.started") {
            if ($activeCommands.Count -gt 0) { return $true }
            if (-not [string]::IsNullOrWhiteSpace($itemId)) { $null = $activeCommands.Add($itemId) }
        }
        elseif (-not [string]::IsNullOrWhiteSpace($itemId)) {
            $null = $activeCommands.Remove($itemId)
        }
    }
    return $false
}
function Get-RequiredContextPaths {
    param([string] $Condition)
    $paths = @(
        ".agents/skills/benchmark/SKILL.md"
    )
    if ($Condition -eq "roslynkit") {
        $paths += @(
            ".agents/skills/roslynkit/SKILL.md",
            ".agents/skills/roslynkit/references/commands.md",
            ".agents/skills/roslynkit/references/output.md"
        )
    }
    return $paths
}
function Get-ContextPathPattern {
    param([string] $ContextPath)
    $escapedPath = [regex]::Escape($ContextPath).Replace("/", "[\\/]")
    return '(?i)(?<![A-Za-z0-9_.-])(?:\.[\\/])?' + $escapedPath + '(?![A-Za-z0-9_.\\/\\-])'
}
function Test-CommandReferencesContextPath {
    param([string] $Command, [string] $ContextPath)
    $normalizedCommand = $Command.Replace("\\", "\").Replace("//", "/")
    return [regex]::IsMatch($normalizedCommand, (Get-ContextPathPattern -ContextPath $ContextPath))
}
function Test-CommandReadsContextPath {
    param([string] $Command, [string] $ContextPath, [int] $Depth = 0)
    if ($Depth -gt 4) {
        return $false
    }
    if (-not (Test-CommandReferencesContextPath -Command $Command -ContextPath $ContextPath)) {
        return $false
    }
    $Command = $Command.Replace("\\", "\").Replace("//", "/")
    $parseInput = if ($Command.Trim() -match '^["'']') { "& $Command" } else { $Command }
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($parseInput, [ref] $tokens, [ref] $parseErrors)
    $pathPattern = Get-ContextPathPattern -ContextPath $ContextPath
    $commandAsts = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst]
            }, $true))
    foreach ($commandAst in $commandAsts) {
        $commandName = $commandAst.GetCommandName()
        if ([string]::IsNullOrWhiteSpace($commandName)) {
            continue
        }
        try {
            $commandFile = [IO.Path]::GetFileName($commandName)
        }
        catch {
            $commandFile = ""
        }
        if ([StringComparer]::OrdinalIgnoreCase.Equals($commandFile, "Get-Content") -and
            $commandAst.Extent.Text -match '(?i)(?:^|\s)-Raw(?:\s|$)' -and
            [regex]::IsMatch($commandAst.Extent.Text, $pathPattern)) {
            return $true
        }
        if ($commandFile -in @("pwsh", "pwsh.exe", "powershell", "powershell.exe")) {
            $payloadMatch = [regex]::Match($commandAst.Extent.Text, '(?is)\s-(?:Command|c)\s+(?<payload>.+)$')
            if ($payloadMatch.Success) {
                $payload = $payloadMatch.Groups["payload"].Value.Trim()
                if ($payload.Length -ge 2 -and (($payload[0] -eq '"' -and $payload[$payload.Length - 1] -eq '"') -or
                        ($payload[0] -eq "'" -and $payload[$payload.Length - 1] -eq "'"))) {
                    $payload = $payload.Substring(1, $payload.Length - 2)
                }
                if (Test-CommandReadsContextPath -Command $payload -ContextPath $ContextPath -Depth ($Depth + 1)) {
                    return $true
                }
            }
        }
    }
    return $false
}
function Test-ForbiddenContextSurface {
    param([string] $Condition, [string] $Command, [bool] $UsesRoslynKit)
    $Command = $Command.Replace("\\", "\").Replace("//", "/")
    $commandWithoutNegativeGlobs = [regex]::Replace($Command, '![^\s]+', '')
    $commandWithoutExclusionFilters = [regex]::Replace($commandWithoutNegativeGlobs, '(?is)-notin\s+@\([^)]*\)', '')
    if ($commandWithoutExclusionFilters -match "(?i)(CODEX_HOME|\.codex|\.claude(?:[\\/]|$)|\.github[\\/](?:skills(?:[\\/]|$)|copilot-instructions\.md)|AGENTS\.md|CLAUDE\.md|MEMORY\.md|rollout-|history\.jsonl|atlas-(csharp|doc|test)-mapper|benchmarks[\\/]|scripts[\\/]benchmark-codex\.ps1|artifacts[\\/]codex-benchmark(?:[\\/]|$)|docs[\\/]agents(?:[\\/]|$)|docs[\\/]local-repository-reference\.md|benchmark-codex|codex-cases\.json|token-efficiency-benchmark)") {
        return $true
    }
    $remainingCommand = $commandWithoutExclusionFilters
    foreach ($allowedPath in Get-RequiredContextPaths -Condition $Condition) {
        $remainingCommand = [regex]::Replace($remainingCommand, (Get-ContextPathPattern -ContextPath $allowedPath), '')
    }
    if ($Condition -eq "roslynkit" -and $UsesRoslynKit) {
        $allowedIndexArgumentPattern = '(?i)--index-path(?:\s+|=)["'']?(?:\.[\\/])?artifacts[\\/]roslynkit\.db["'']?'
        $remainingCommand = [regex]::Replace($remainingCommand, $allowedIndexArgumentPattern, '')
    }
    return $remainingCommand -match '(?i)\.agents(?:[\\/]|$)|(?:^|[^A-Za-z0-9_.-])artifacts[\\/]|AGENTS\.md'
}
function Get-ComplianceIssues {
    param([string] $Condition, [string[]] $Commands, [object[]] $Events, [string[]] $RepositoryChanges, [string] $ResolvedRoslynKitPath)
    $issues = New-Object System.Collections.Generic.List[string]
    $observedRoslynKit = @($Commands | Where-Object {
            Test-RoslynKitInvocation -Command $_ -ResolvedRoslynKitPath $ResolvedRoslynKitPath
        }).Count -gt 0
    foreach ($command in $Commands) {
        $usesRoslynKit = Test-RoslynKitInvocation -Command $command -ResolvedRoslynKitPath $ResolvedRoslynKitPath
        if ($Condition -eq "raw-codex" -and $usesRoslynKit) {
            $issues.Add("raw-codex invoked RoslynKit: $command")
        }
        if ($command -match "(?i)\b(curl|wget|Invoke-WebRequest|Invoke-RestMethod)\b|https?://") {
            $issues.Add("used web or network access: $command")
        }
        if (Test-ForbiddenContextSurface -Condition $Condition -Command $command -UsesRoslynKit $usesRoslynKit) {
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
    if ($Condition -eq "roslynkit" -and -not $observedRoslynKit) {
        $issues.Add("RoslynKit condition did not invoke RoslynKit")
    }
    if ($Condition -eq "roslynkit" -and (Test-ConcurrentRoslynKitInvocations -Events $Events -ResolvedRoslynKitPath $ResolvedRoslynKitPath)) {
        $issues.Add("RoslynKit commands overlapped; run one invocation at a time")
    }
    $requiredReadIndices = @{}
    foreach ($requiredPath in Get-RequiredContextPaths -Condition $Condition) {
        $readIndex = -1
        for ($commandIndex = 0; $commandIndex -lt $Commands.Count; $commandIndex++) {
            if (Test-CommandReadsContextPath -Command $Commands[$commandIndex] -ContextPath $requiredPath) {
                $readIndex = $commandIndex
                break
            }
        }
        $requiredReadIndices[$requiredPath] = $readIndex
        if ($readIndex -lt 0) {
            $issues.Add("did not read required context: $requiredPath")
        }
    }
    $benchmarkSkillPath = ".agents/skills/benchmark/SKILL.md"
    if ($requiredReadIndices[$benchmarkSkillPath] -gt 0) {
        $issues.Add("benchmark skill was not read by the first command")
    }
    if ($Condition -eq "roslynkit") {
        $firstRoslynKitCommandIndex = -1
        for ($commandIndex = 0; $commandIndex -lt $Commands.Count; $commandIndex++) {
            if (Test-RoslynKitInvocation -Command $Commands[$commandIndex] -ResolvedRoslynKitPath $ResolvedRoslynKitPath) {
                $firstRoslynKitCommandIndex = $commandIndex
                break
            }
        }
        if ($firstRoslynKitCommandIndex -ge 0) {
            foreach ($requiredPath in Get-RequiredContextPaths -Condition $Condition) {
                if ($requiredReadIndices[$requiredPath] -ge $firstRoslynKitCommandIndex) {
                    $issues.Add("required context was not read before RoslynKit invocation: $requiredPath")
                }
            }
        }
    }
    if ($Commands.Count -eq 0) {
        $issues.Add("run recorded no inspection commands")
    }
    if ($RepositoryChanges.Count -gt 0) {
        $issues.Add("repository content changed: $($RepositoryChanges -join '; ')")
    }
    return @($issues | Select-Object -Unique)
}
function Test-NonEmptyFile {
    param([string] $Path)
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and -not [string]::IsNullOrWhiteSpace((Get-Content -Raw -LiteralPath $Path))
}
function Get-RepositoryContentManifest {
    param([string] $RepoRoot)
    $paths = @(& git -C $RepoRoot ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list nonignored repository files for the content manifest."
    }
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($relativePath in $paths) {
        $fullPath = Join-Path $RepoRoot $relativePath
        $exists = Test-Path -LiteralPath $fullPath -PathType Leaf
        $sha256 = if ($exists) { (Get-FileHash -Algorithm SHA256 -LiteralPath $fullPath).Hash } else { $null }
        $entries.Add([pscustomobject]@{
                path = $relativePath.Replace("\", "/")
                exists = $exists
                sha256 = $sha256
            })
    }
    return @($entries.ToArray() | Sort-Object path)
}
function ConvertTo-RepositoryManifestRecord {
    param([object] $Entry)
    return "{0}|{1}|{2}" -f $Entry.path, $Entry.exists, $Entry.sha256
}
function Get-RepositoryContentChanges {
    param([string] $RepoRoot, [object[]] $Baseline)
    $current = Get-RepositoryContentManifest -RepoRoot $RepoRoot
    $baselineRecords = @($Baseline | ForEach-Object { ConvertTo-RepositoryManifestRecord -Entry $_ })
    $currentRecords = @($current | ForEach-Object { ConvertTo-RepositoryManifestRecord -Entry $_ })
    return @(Compare-Object -ReferenceObject $baselineRecords -DifferenceObject $currentRecords |
        ForEach-Object { $_.InputObject })
}
function Invoke-BenchmarkRun {
    param([object] $Case, [string] $Condition, [int] $Trial, [string] $RepoRoot, [object[]] $RepositoryManifest, [string] $RunRoot, [string] $ResolvedRoslynKitPath, [string[]] $DisabledFeatures)
    $runId = "{0}-{1}-trial{2}" -f $Case.id, $Condition, $Trial
    $answerPath = Join-Path $RunRoot "answers\$runId.md"
    $eventPath = Join-Path $RunRoot "events\$runId.jsonl"
    $stderrPath = Join-Path $RunRoot "stderr\$runId.txt"
    $commandsPath = Join-Path $RunRoot "commands\$runId.txt"
    $prompt = New-ConditionPrompt -Condition $Condition -Prompt $Case.prompt
    $arguments = New-CodexArguments -Prompt $prompt -RepoRoot $RepoRoot -AnswerPath $answerPath -DisabledFeatures $DisabledFeatures
    $exitCode = -1
    $startedAt = [DateTime]::UtcNow
    $completedAt = $startedAt
    if ((Get-RepositoryContentChanges -RepoRoot $RepoRoot -Baseline $RepositoryManifest).Count -gt 0) {
        throw "Repository content changed before '$runId'."
    }
    try {
        $startedAt = [DateTime]::UtcNow
        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        Push-Location $RepoRoot
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
    $events = Read-Events -Path $eventPath
    $commands = Get-Commands -Events $events
    $commands | Set-Content -LiteralPath $commandsPath -Encoding UTF8
    $usage = Get-TokenUsage -Events $events
    $repositoryChanges = Get-RepositoryContentChanges -RepoRoot $RepoRoot -Baseline $RepositoryManifest
    $issues = Get-ComplianceIssues -Condition $Condition -Commands $commands -Events $events -RepositoryChanges $repositoryChanges -ResolvedRoslynKitPath $ResolvedRoslynKitPath
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
    param([string] $RepoRoot, [object[]] $RepositoryManifest, [string] $RunRoot, [string] $ResolvedRoslynKitPath, [string[]] $DisabledFeatures)
    $preflightRoot = Join-Path $RunRoot "preflight"
    New-Item -ItemType Directory -Force -Path $preflightRoot | Out-Null
    $answerPath = Join-Path $preflightRoot "answer.md"
    $eventPath = Join-Path $preflightRoot "events.jsonl"
    $stderrPath = Join-Path $preflightRoot "stderr.txt"
    $commandsPath = Join-Path $preflightRoot "commands.txt"
    $preflightCommand = "rg --version; roslynkit --version"
    $prompt = "Run exactly these two PowerShell commands once:`n`n$preflightCommand`n`nThen reply with exactly both version outputs and exit codes, and nothing else."
    $arguments = New-CodexArguments -Prompt $prompt -RepoRoot $RepoRoot -AnswerPath $answerPath -DisabledFeatures $DisabledFeatures
    $exitCode = -1
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        Push-Location $RepoRoot
        try {
            & codex @arguments 1> $eventPath 2> $stderrPath
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
    }
    finally {
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
            [string] $_.item.command -match '(?i)\brg(?:\.exe)?\s+--version\b'
        })
    $preflightOutput = ($completedCommands | ForEach-Object { [string] $_.item.aggregated_output }) -join [Environment]::NewLine
    if ($exitCode -ne 0 -or $completedCommands.Count -ne 1 -or $failedCommands.Count -gt 0 -or $successfulRoslynKitCommands.Count -ne 1 -or
        $successfulRipgrepCommands.Count -ne 1 -or $preflightOutput -notmatch "(?im)^(?:ripgrep|rg)\s+\d" -or $preflightOutput -notmatch "(?i)roslynkit version") {
        throw "Benchmark preflight failed before measured sessions. Inspect '$preflightRoot'."
    }
    $repositoryChanges = Get-RepositoryContentChanges -RepoRoot $RepoRoot -Baseline $RepositoryManifest
    if ($repositoryChanges.Count -gt 0) {
        throw "Repository content changed during benchmark preflight: $($repositoryChanges -join '; ')"
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
if ($MyInvocation.InvocationName -eq ".") {
    return
}
[Environment]::SetEnvironmentVariable("CODEX_THREAD_ID", $null, "Process")
$repoRoot = Resolve-RepoRoot
$resolvedRoslynKitPath = Resolve-GlobalRoslynKitPath
$resolvedRipgrepPath = Resolve-GlobalRipgrepPath
$cases = Get-SelectedCases -Cases (Get-CaseData -RepoRoot $repoRoot) -SelectedCaseId $CaseId
$activeCodexConfigPath = Set-WorkstationCodexHome
if ($DryRun) {
    $placeholderRepoRoot = "<repository-root>"
    $disabledFeatures = Get-DisabledFeatures -DryRunMode
    Write-Host "Active Codex config: $activeCodexConfigPath"
    Write-Host "Environment: the workstation CODEX_HOME is used directly; benchmark-specific command-line overrides remain in effect."
    Write-Host "Execution: child sessions bypass approvals and sandboxing, inherit the full workstation environment, and use the repository root as the --cd working root."
    Write-Host "RoslynKit condition: the global 'roslynkit' command is resolved from the inherited workstation PATH; the prepared search index is ./artifacts/roslynkit.db relative to the repository root."
    Write-Host "Preflight: one unmeasured command must run global 'rg --version' and 'roslynkit --version' successfully before any measured session starts."
    Write-Host "Validity: an invalid measured session is recorded and excluded from comparison, then the remaining scheduled sessions continue without retry. Preparation, preflight, and nonignored repository content changes stop the controller."
    Write-Host "Repository integrity: a content manifest is captured after preparation and validated after preflight and every measured session; ignored artifacts do not affect it."
    Write-Host ""
    foreach ($trial in 1..$Trials) {
        $conditions = if (($trial % 2) -eq 1) { @("raw-codex", "roslynkit") } else { @("roslynkit", "raw-codex") }
        foreach ($case in $cases) {
            foreach ($condition in $conditions) {
                $prompt = New-ConditionPrompt -Condition $condition -Prompt $case.prompt
                $arguments = New-CodexArguments -Prompt $prompt -RepoRoot $placeholderRepoRoot -AnswerPath "<artifacts-answer-path>" -DisabledFeatures $disabledFeatures
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
$rows = New-Object System.Collections.Generic.List[object]
try {
    foreach ($directory in @($runRoot, (Join-Path $runRoot "answers"), (Join-Path $runRoot "events"), (Join-Path $runRoot "stderr"), (Join-Path $runRoot "commands"))) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    Restore-RepositoryDependencies -RepoRoot $repoRoot
    Initialize-RoslynKitIndex -RepoRoot $repoRoot -ResolvedRoslynKitPath $resolvedRoslynKitPath
    Stop-RoslynKitDaemon -RepoRoot $repoRoot -ResolvedRoslynKitPath $resolvedRoslynKitPath
    $repositoryManifest = Get-RepositoryContentManifest -RepoRoot $repoRoot
    Invoke-BenchmarkPreflight -RepoRoot $repoRoot -RepositoryManifest $repositoryManifest -RunRoot $runRoot -ResolvedRoslynKitPath $resolvedRoslynKitPath -DisabledFeatures $disabledFeatures
    foreach ($trial in 1..$Trials) {
        $conditions = if (($trial % 2) -eq 1) { @("raw-codex", "roslynkit") } else { @("roslynkit", "raw-codex") }
        foreach ($case in $cases) {
            foreach ($condition in $conditions) {
                try {
                    $row = Invoke-BenchmarkRun -Case $case -Condition $condition -Trial $trial -RepoRoot $repoRoot -RepositoryManifest $repositoryManifest -RunRoot $runRoot -ResolvedRoslynKitPath $resolvedRoslynKitPath -DisabledFeatures $disabledFeatures
                    $rows.Add($row)
                    Write-Reports -RunRoot $runRoot -Rows $rows -Cases $cases
                    $repositoryChanges = Get-RepositoryContentChanges -RepoRoot $repoRoot -Baseline $repositoryManifest
                    if ($repositoryChanges.Count -gt 0) {
                        throw "Repository content changed during '$($row.case_id)/$($row.condition)/trial$($row.trial)': $($repositoryChanges -join '; ')"
                    }
                    if (-not $row.valid) {
                        Write-Warning "Recorded invalid session '$($row.case_id)/$($row.condition)/trial$($row.trial)' and continuing: $($row.issues)"
                    }
                }
                finally {
                    if ($condition -eq "roslynkit") {
                        Stop-RoslynKitDaemon -RepoRoot $repoRoot -ResolvedRoslynKitPath $resolvedRoslynKitPath
                    }
                }
            }
        }
    }
}
finally {
    Stop-RoslynKitDaemon -RepoRoot $repoRoot -ResolvedRoslynKitPath $resolvedRoslynKitPath
}
$invalidRows = @($rows | Where-Object { -not $_.valid })
if ($invalidRows.Count -gt 0) {
    Write-Warning "Benchmark completed with $($invalidRows.Count) invalid measured session(s). Review '$runRoot\summary.md'; comparisons use valid rows only."
}
Write-Host "Benchmark complete: $runRoot"
