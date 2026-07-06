[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int] $Trials = 1,

    [ValidateSet("all", "fixture", "repo")]
    [string] $BenchmarkSet = "repo",

    [ValidateSet("all", "fixture-symbol", "repo-dispatch", "repo-references", "repo-references-flow")]
    [string] $CaseId = "repo-references-flow",

    [string] $Model = "gpt-5.5",

    [string] $RoslynKitPath,

    [string] $RoslynKitSkillPath,

    [switch] $DryRun
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    $root = & git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($root)) {
        return (Resolve-Path $root).Path
    }

    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Resolve-RoslynKitPath {
    param([string] $Candidate)

    if (-not [string]::IsNullOrWhiteSpace($Candidate)) {
        if (Test-Path -LiteralPath $Candidate) {
            return (Resolve-Path $Candidate).Path
        }

        return $Candidate
    }

    $toolDir = Join-Path (Join-Path (Join-Path $HOME ".roslynkit") "tools") "roslynkit-dev"
    $exeName = if ($env:OS -eq "Windows_NT") { "roslynkit.exe" } else { "roslynkit" }
    return (Join-Path $toolDir $exeName)
}

function Resolve-RoslynKitSkillPath {
    param(
        [string] $RepoRoot,
        [string] $Candidate
    )

    if (-not [string]::IsNullOrWhiteSpace($Candidate)) {
        if (Test-Path -LiteralPath $Candidate) {
            return (Resolve-Path $Candidate).Path
        }

        return $Candidate
    }

    return (Join-Path (Join-Path (Join-Path $RepoRoot ".agents") "skills") "roslynkit-dev\SKILL.md")
}

function Test-RoslynKitExecutable {
    param([string] $CommandOrPath)

    if ([string]::IsNullOrWhiteSpace($CommandOrPath)) {
        return $false
    }

    if (Test-Path -LiteralPath $CommandOrPath) {
        return $true
    }

    return $null -ne (Get-Command $CommandOrPath -ErrorAction SilentlyContinue)
}

function ConvertTo-BenchmarkPromptPath {
    param(
        [string] $Path,
        [string] $RepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }

    $normalizedPath = $Path.Replace("/", "\")
    $normalizedRepoRoot = $RepoRoot.Replace("/", "\").TrimEnd("\")
    if ($normalizedPath.StartsWith($normalizedRepoRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $normalizedPath.Substring($normalizedRepoRoot.Length + 1)
        return ".\$relative"
    }

    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $normalizedUserProfile = $env:USERPROFILE.Replace("/", "\").TrimEnd("\")
        if ($normalizedPath.StartsWith($normalizedUserProfile + "\", [StringComparison]::OrdinalIgnoreCase)) {
            return ('${env:USERPROFILE}' + $normalizedPath.Substring($normalizedUserProfile.Length))
        }
    }

    return $Path
}

function Get-CodexHome {
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        return $env:CODEX_HOME
    }

    return (Join-Path $HOME ".codex")
}

function Get-RolloutFiles {
    $codexHome = Get-CodexHome
    $roots = @(
        (Join-Path $codexHome "sessions"),
        (Join-Path $codexHome "archived_sessions")
    )

    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) {
            Get-ChildItem -LiteralPath $root -Recurse -Force -Filter "rollout-*.jsonl" -ErrorAction SilentlyContinue
        }
    }
}

function Get-RolloutSnapshot {
    $snapshot = @{}
    foreach ($file in Get-RolloutFiles) {
        $snapshot[$file.FullName] = $file.LastWriteTimeUtc
    }

    return $snapshot
}

function Find-NewRolloutFile {
    param(
        [hashtable] $Before,
        [datetime] $StartedAtUtc
    )

    $files = @(Get-RolloutFiles | Where-Object {
            (-not $Before.ContainsKey($_.FullName)) -and $_.LastWriteTimeUtc -ge $StartedAtUtc.AddMinutes(-1)
        } | Sort-Object LastWriteTimeUtc -Descending)

    if ($files.Count -gt 0) {
        return $files[0].FullName
    }

    $fallback = @(Get-RolloutFiles | Where-Object {
            $_.LastWriteTimeUtc -ge $StartedAtUtc.AddMinutes(-1)
        } | Sort-Object LastWriteTimeUtc -Descending)

    if ($fallback.Count -gt 0) {
        return $fallback[0].FullName
    }

    return $null
}

function Read-JsonLines {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $line | ConvertFrom-Json
        }
        catch {
            # Codex stdout/stderr can contain non-JSON diagnostics on failure.
        }
    }
}

function Get-LastTokenUsage {
    param([string[]] $Paths)

    $last = $null
    foreach ($path in $Paths) {
        foreach ($record in Read-JsonLines -Path $path) {
            if ($record.type -eq "turn.completed" -and $null -ne $record.usage) {
                $usage = $record.usage
                $last = [pscustomobject]@{
                    input_tokens             = $usage.input_tokens
                    cached_input_tokens      = $usage.cached_input_tokens
                    output_tokens            = $usage.output_tokens
                    reasoning_output_tokens  = $usage.reasoning_output_tokens
                    total_tokens             = ([long] $usage.input_tokens) + ([long] $usage.output_tokens)
                }
            }

            if ($null -eq $record.payload) {
                continue
            }

            if ($record.payload.type -eq "token_count" -and $null -ne $record.payload.info.total_token_usage) {
                $last = $record.payload.info.total_token_usage
            }
        }
    }

    return $last
}

function Get-CodexShellCommands {
    param([string[]] $Paths)

    $toolCommands = New-Object System.Collections.Generic.List[string]
    $eventCommands = New-Object System.Collections.Generic.List[string]
    foreach ($path in $Paths) {
        foreach ($record in Read-JsonLines -Path $path) {
            if (($record.type -eq "item.started" -or $record.type -eq "item.completed") -and
                $null -ne $record.item -and
                $record.item.type -eq "command_execution" -and
                -not [string]::IsNullOrWhiteSpace($record.item.command)) {
                $eventCommands.Add([string] $record.item.command)
                continue
            }

            if ($record.type -ne "response_item" -or $null -eq $record.payload) {
                continue
            }

            if ($record.payload.type -ne "function_call") {
                continue
            }

            if (@("shell_command", "exec_command", "shell") -notcontains $record.payload.name) {
                continue
            }

            $commandText = $null
            if (-not [string]::IsNullOrWhiteSpace($record.payload.arguments)) {
                try {
                    $arguments = $record.payload.arguments | ConvertFrom-Json
                    if ($null -ne $arguments.command) {
                        $commandText = [string] $arguments.command
                    }
                }
                catch {
                    $commandText = [string] $record.payload.arguments
                }
            }

            if (-not [string]::IsNullOrWhiteSpace($commandText)) {
                $toolCommands.Add($commandText)
            }
        }
    }

    if ($toolCommands.Count -gt 0) {
        return @($toolCommands)
    }

    return @($eventCommands)
}

function Get-ForbiddenExternalCommandViolations {
    param([string] $Command)

    $violations = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($Command)) {
        return @($violations)
    }

    $normalizedCommand = $Command.Replace("/", "\")
    $repoLocalMemoryDirectoryPattern = ('\.' + 'syn' + 'apse(\\|$|[\s"`''])')
    $repoLocalMemoryToolPattern = ('(?i)(^|[\s;&|"`''])' + 'syn' + 'apse' + '([\s;&|"`'']|$)')
    $memoryOrSessionPattern = '(?i)(\.codex\\memories(\\|$|[\s"`''])|\\memories\\MEMORY\.md|(^|[\\\s"`''])MEMORY\.md([\\\s"`'']|$)|rollout_summaries|\.codex\\sessions(\\|$|[\s"`''])|\.codex\\archived_sessions(\\|$|[\s"`''])|(^|[\\\s"`''])history\.jsonl([\\\s"`'']|$)|rollout-[^\\\s"`'']+\.jsonl)'
    $atlasPattern = '(?i)(\.codex\\atlas(\\|$|[\s"`''])|(^|[\s;&|"`''])(atlas-router|atlas-csharp-mapper)([\s;&|"`'']|$))'
    $subagentPattern = '(?i)(^|[\s;&|"`''])(scout|explorer|worker)([\s;&|"`'']|$)'

    if ($normalizedCommand -match $memoryOrSessionPattern) {
        $violations.Add("used forbidden memory/session artifact: $Command")
    }

    if ($normalizedCommand -match $repoLocalMemoryDirectoryPattern -or $Command -match $repoLocalMemoryToolPattern) {
        $violations.Add("used forbidden repo-local memory/cache artifact or tool: $Command")
    }

    if ($normalizedCommand -match $atlasPattern) {
        $violations.Add("used forbidden Atlas artifact/tool: $Command")
    }

    if ($Command -match $subagentPattern) {
        $violations.Add("used forbidden subagent/tool: $Command")
    }

    return @($violations)
}

function Test-ForbiddenExternalToolName {
    param([string] $Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return $false
    }

    $repoLocalMemoryToolName = 'syn' + 'apse'
    return $Name -match '(?i)(^|[._:-])(scout|explorer|worker)($|[._:-])|atlas-router|atlas-csharp-mapper' -or
        $Name -match "(?i)(^|[._:-])$repoLocalMemoryToolName($|[._:-])"
}

function Get-ForbiddenExternalToolViolations {
    param([string[]] $Paths)

    $violations = New-Object System.Collections.Generic.List[string]
    foreach ($path in $Paths) {
        foreach ($record in Read-JsonLines -Path $path) {
            if ($record.type -eq "response_item" -and
                $null -ne $record.payload -and
                $record.payload.type -eq "function_call") {
                $toolName = [string] $record.payload.name
                if (Test-ForbiddenExternalToolName -Name $toolName) {
                    $violations.Add("used forbidden external tool: $toolName")
                }

                if (@("shell_command", "exec_command", "shell") -notcontains $toolName) {
                    $arguments = [string] $record.payload.arguments
                    $repoLocalMemoryToolName = 'syn' + 'apse'
                    if ($arguments -match '(?i)\b(scout|explorer|worker|atlas-router|atlas-csharp-mapper)\b' -or
                        $arguments -match "(?i)\b$repoLocalMemoryToolName\b") {
                        $violations.Add("used forbidden external tool arguments: $toolName")
                    }
                }
            }

            if (($record.type -eq "item.started" -or $record.type -eq "item.completed") -and
                $null -ne $record.item) {
                $itemName = [string] $record.item.name
                if (Test-ForbiddenExternalToolName -Name $itemName) {
                    $violations.Add("used forbidden external tool: $itemName")
                }
            }
        }
    }

    return @($violations | Select-Object -Unique)
}

function Test-RoslynKitCommandInvocation {
    param([string] $Command)

    if ([string]::IsNullOrWhiteSpace($Command)) {
        return $false
    }

    $namedInvocationPattern = '(?i)(^|[;&]\s*)(&\s*)?(\$roslynkit(Dev)?\b|(roslynkit|roslynkit-dev)(\.exe)?(\s|$))'
    if ($Command -match $namedInvocationPattern) {
        return $true
    }

    $pathInvocationPattern = '(?i)(^|[;&]\s*)(&\s*)?("[^"]*(\\|/)roslynkit\.exe"|''[^'']*(\\|/)roslynkit\.exe''|[^\s;&|]*(\\|/)roslynkit\.exe)(\s|$)'
    if ($Command -match $pathInvocationPattern) {
        return $true
    }

    return $false
}

function Test-DotNetRunRoslynKitInvocation {
    param([string] $Command)

    return -not [string]::IsNullOrWhiteSpace($Command) -and
        $Command -match '(?i)(^|[;&]\s*)dotnet(\.exe)?\s+run\b.*RoslynKit'
}

function Get-RoslynKitSemanticCommandFailures {
    param([string[]] $Paths)

    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($path in $Paths) {
        foreach ($record in Read-JsonLines -Path $path) {
            if ($record.type -ne "item.completed" -or
                $null -eq $record.item -or
                $record.item.type -ne "command_execution" -or
                [string]::IsNullOrWhiteSpace($record.item.command)) {
                continue
            }

            $command = [string] $record.item.command
            if (-not (Test-RoslynKitCommandInvocation -Command $command)) {
                continue
            }

            if ($command -match "(?i)(\shelp(\s|`"|$)|--version)") {
                continue
            }

            $output = [string] $record.item.aggregated_output
            $status = [string] $record.item.status
            $exitCode = $record.item.exit_code
            $failed = $false
            if ($status -eq "failed") {
                $failed = $true
            }

            if ($null -ne $exitCode -and [int] $exitCode -ne 0) {
                $failed = $true
            }

            if ($output -match "(?i)(UnauthorizedAccessException|RemoteInvocationException|Access to the path .* is denied|MSBuild workspace load failed)" -or
                $output -match "(?im)^error:\s") {
                $failed = $true
            }

            if ($failed) {
                $failures.Add(("roslynkit semantic command failed status={0} exit={1}: {2}" -f $status, $exitCode, $command))
            }
        }
    }

    return @($failures | Select-Object -Unique)
}

function Get-AnswerFailureSignals {
    param([string] $AnswerPath)

    $signals = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($AnswerPath) -or -not (Test-Path -LiteralPath $AnswerPath)) {
        return @($signals)
    }

    $text = Get-Content -LiteralPath $AnswerPath -Raw
    $patterns = @(
        "UnauthorizedAccessException",
        "Access to the path .* is denied",
        "couldn['’]?t complete",
        "could not complete",
        "blocked before",
        "blocked by",
        "failed before",
        "MSBuild workspace load failed",
        "no .* evidence was produced",
        "can['’]?t truthfully",
        "cannot truthfully",
        "\bFAILED\b"
    )

    foreach ($pattern in $patterns) {
        if ($text -match "(?i)$pattern") {
            $signals.Add("answer contains failure signal: $pattern")
        }
    }

    return @($signals | Select-Object -Unique)
}

function Test-RoslynKitSkillReadCommand {
    param(
        [string] $Command,
        [string] $RoslynKitSkillPath
    )

    if ([string]::IsNullOrWhiteSpace($Command) -or [string]::IsNullOrWhiteSpace($RoslynKitSkillPath)) {
        return $false
    }

    $readToolPattern = "(?i)(Get-Content|ReadAllText|ReadAllLines|(^|[\s;&|])gc(\s|$)|(^|[\s;&|])cat(\s|$)|(^|[\s;&|])type(\s|$)|(^|[\s;&|])more(\.com)?(\s|$))"
    if ($Command -notmatch $readToolPattern) {
        return $false
    }

    $normalizedCommand = $Command.Replace("/", "\")
    $normalizedSkillPath = $RoslynKitSkillPath.Replace("/", "\")
    $relativeSkillPath = ".\.agents\skills\roslynkit-dev\SKILL.md"

    $containsSkillPath = $normalizedCommand.IndexOf($normalizedSkillPath, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $normalizedCommand.IndexOf($relativeSkillPath, [StringComparison]::OrdinalIgnoreCase) -ge 0

    if (-not $containsSkillPath) {
        return $false
    }

    $nonSkillReadPattern = "(?i)(MEMORY\.md|AGENTS\.md|CLAUDE\.md|\.codex|\\src\\|\\tests\\|\\docs\\|\.agents\\skills\\roslynkit\\SKILL\.md)"
    return $normalizedCommand -notmatch $nonSkillReadPattern
}

function Get-CommandViolations {
    param(
        [string] $Arm,
        [string[]] $Commands,
        [string] $RoslynKitSkillPath
    )

    $violations = New-Object System.Collections.Generic.List[string]
    $usesRoslynKit = $false

    foreach ($command in $Commands) {
        $violations.AddRange([string[]] @(Get-ForbiddenExternalCommandViolations -Command $command))

        $usesRoslynKitCommand = Test-RoslynKitCommandInvocation -Command $command
        if ($usesRoslynKitCommand) {
            $usesRoslynKit = $true
        }

        if ($Arm -eq "baseline") {
            if ($usesRoslynKitCommand) {
                $violations.Add("baseline used RoslynKit: $command")
            }

            if (Test-DotNetRunRoslynKitInvocation -Command $command) {
                $violations.Add("baseline used dotnet run for RoslynKit: $command")
            }
        }
        elseif ($Arm -eq "roslynkit") {
            $forbiddenTextTool = $command -match "(?i)(Get-Content|Select-String|ReadAllText|ReadAllLines|(^|[\s;&|])rg(\.exe)?(\s|$)|(^|[\s;&|])grep(\.exe)?(\s|$)|(^|[\s;&|])gc(\s|$)|(^|[\s;&|])cat(\s|$)|(^|[\s;&|])type(\s|$)|(^|[\s;&|])more(\.com)?(\s|$))"
            $allowedSkillRead = Test-RoslynKitSkillReadCommand -Command $command -RoslynKitSkillPath $RoslynKitSkillPath
            if ($forbiddenTextTool -and -not $allowedSkillRead) {
                $violations.Add("roslynkit arm used text/source inspection: $command")
            }

        }
    }

    if ($Arm -eq "roslynkit" -and -not $usesRoslynKit) {
        $violations.Add("roslynkit arm did not issue a RoslynKit command")
    }

    return @($violations)
}

function Get-BenchmarkCases {
    param(
        [string] $RepoRoot,
        [string] $RoslynKitPath,
        [string] $RoslynKitSkillPath
    )

    $fixtureBaseline = @'
Read-only token benchmark run.
Goal: In this repository, explain what FixtureApp.Consumer.Run returns and identify which concrete type implements FixtureApp.IMessageSource.
Constraints:
- Do not edit files.
- Do not read Codex memory or prior-session artifacts: ${env:USERPROFILE}\.codex\memories, .codex\memories, .codex\sessions, .codex\archived_sessions, history.jsonl, MEMORY.md, rollout_summaries, or rollout-*.jsonl.
- Do not use repo-local memory/cache tools or generated repo-local memory/cache directories.
- Do not use Atlas files or tools: .codex\atlas, atlas-router, atlas-csharp-mapper, or Atlas scripts.
- Do not use subagents such as scout, explorer, or worker.
- Do not use RoslynKit, roslynkit-dev, dotnet run, dotnet test, Atlas scripts, subagents, or web search.
- Use only native shell/text inspection such as rg, Get-Content, Select-String, and ordinary PowerShell.
- Stop as soon as enough evidence is available.
Return a concise answer with the inspected evidence paths.
'@

    $fixtureRoslynKit = @'
Read-only token benchmark run.
Goal: In this repository, explain what FixtureApp.Consumer.Run returns and identify which concrete type implements FixtureApp.IMessageSource.
Constraints:
- Do not edit files.
- Do not read Codex memory or prior-session artifacts: ${env:USERPROFILE}\.codex\memories, .codex\memories, .codex\sessions, .codex\archived_sessions, history.jsonl, MEMORY.md, rollout_summaries, or rollout-*.jsonl.
- Do not use repo-local memory/cache tools or generated repo-local memory/cache directories.
- Do not use Atlas files or tools: .codex\atlas, atlas-router, atlas-csharp-mapper, or Atlas scripts.
- Do not use subagents such as scout, explorer, or worker.
- Before C# inspection, read the repo-local RoslynKit dev skill once using exactly this PowerShell command: Get-Content -Raw -LiteralPath '{ROSLYNKIT_SKILL_PATH}'
- Follow that skill's command and token-discipline guidance.
- Apart from that one skill read, do not use rg, Get-Content, Select-String, cat, type, grep, more, [System.IO.File]::ReadAllText, [System.IO.File]::ReadAllLines, Atlas scripts, subagents, or web search.
- Do not read AGENTS.md, memory files, docs, or any other skill file.
- Use this RoslynKit dev executable for C# inspection: {ROSLYNKIT_PATH}
- Always pass --target explicitly.
- Use .\tests\FixtureWorkspace\App\App.csproj as the fixture target when inspecting FixtureApp symbols.
- Prefer symbol-source, implementations, references, quick-info, and symbols over full document reads.
- Stop as soon as enough evidence is available.
Return a concise answer with the RoslynKit commands or symbol ids that support it.
'@

    $dispatchBaseline = @'
Read-only token benchmark run.
Goal: Identify where RoslynKit dispatches the symbol-source command from RoslynCommandExecutor.ExecuteAsync and summarize the smallest relevant flow.
Constraints:
- Do not edit files.
- Do not read Codex memory or prior-session artifacts: ${env:USERPROFILE}\.codex\memories, .codex\memories, .codex\sessions, .codex\archived_sessions, history.jsonl, MEMORY.md, rollout_summaries, or rollout-*.jsonl.
- Do not use repo-local memory/cache tools or generated repo-local memory/cache directories.
- Do not use Atlas files or tools: .codex\atlas, atlas-router, atlas-csharp-mapper, or Atlas scripts.
- Do not use subagents such as scout, explorer, or worker.
- Do not use RoslynKit, roslynkit-dev, dotnet run, dotnet test, Atlas scripts, subagents, or web search.
- Use only native shell/text inspection such as rg, Get-Content, Select-String, and ordinary PowerShell.
- Stop as soon as enough evidence is available.
Return a concise answer with the inspected evidence paths.
'@

    $dispatchRoslynKit = @'
Read-only token benchmark run.
Goal: Identify where RoslynKit dispatches the symbol-source command from RoslynCommandExecutor.ExecuteAsync and summarize the smallest relevant flow.
Constraints:
- Do not edit files.
- Do not read Codex memory or prior-session artifacts: ${env:USERPROFILE}\.codex\memories, .codex\memories, .codex\sessions, .codex\archived_sessions, history.jsonl, MEMORY.md, rollout_summaries, or rollout-*.jsonl.
- Do not use repo-local memory/cache tools or generated repo-local memory/cache directories.
- Do not use Atlas files or tools: .codex\atlas, atlas-router, atlas-csharp-mapper, or Atlas scripts.
- Do not use subagents such as scout, explorer, or worker.
- Before C# inspection, read the repo-local RoslynKit dev skill once using exactly this PowerShell command: Get-Content -Raw -LiteralPath '{ROSLYNKIT_SKILL_PATH}'
- Follow that skill's command and token-discipline guidance.
- Apart from that one skill read, do not use rg, Get-Content, Select-String, cat, type, grep, more, [System.IO.File]::ReadAllText, [System.IO.File]::ReadAllLines, Atlas scripts, subagents, or web search.
- Do not read AGENTS.md, memory files, docs, or any other skill file.
- Use this RoslynKit dev executable for C# inspection: {ROSLYNKIT_PATH}
- Always pass --target .\RoslynKit.slnx for repo symbols.
- Prefer symbols with --max-results 1 and symbol-source over full document reads.
- Stop as soon as enough evidence is available.
Return a concise answer with the RoslynKit commands or symbol ids that support it.
'@

    $referencesBaseline = @'
Read-only token benchmark run.
Goal: Find callers or references of RoslynKit.PositionResolver.GetPositionAsync and report the smallest relevant evidence.
Constraints:
- Do not edit files.
- Do not read Codex memory or prior-session artifacts: ${env:USERPROFILE}\.codex\memories, .codex\memories, .codex\sessions, .codex\archived_sessions, history.jsonl, MEMORY.md, rollout_summaries, or rollout-*.jsonl.
- Do not use repo-local memory/cache tools or generated repo-local memory/cache directories.
- Do not use Atlas files or tools: .codex\atlas, atlas-router, atlas-csharp-mapper, or Atlas scripts.
- Do not use subagents such as scout, explorer, or worker.
- Do not use RoslynKit, roslynkit-dev, dotnet run, dotnet test, Atlas scripts, subagents, or web search.
- Use only native shell/text inspection such as rg, Get-Content, Select-String, and ordinary PowerShell.
- Stop as soon as enough evidence is available.
Return a concise answer with the inspected evidence paths.
'@

    $referencesRoslynKit = @'
Read-only token benchmark run.
Goal: Find callers or references of RoslynKit.PositionResolver.GetPositionAsync and report the smallest relevant evidence.
Constraints:
- Do not edit files.
- Do not read Codex memory or prior-session artifacts: ${env:USERPROFILE}\.codex\memories, .codex\memories, .codex\sessions, .codex\archived_sessions, history.jsonl, MEMORY.md, rollout_summaries, or rollout-*.jsonl.
- Do not use repo-local memory/cache tools or generated repo-local memory/cache directories.
- Do not use Atlas files or tools: .codex\atlas, atlas-router, atlas-csharp-mapper, or Atlas scripts.
- Do not use subagents such as scout, explorer, or worker.
- Before C# inspection, read the repo-local RoslynKit dev skill once using exactly this PowerShell command: Get-Content -Raw -LiteralPath '{ROSLYNKIT_SKILL_PATH}'
- Follow that skill's command and token-discipline guidance.
- Apart from that one skill read, do not use rg, Get-Content, Select-String, cat, type, grep, more, [System.IO.File]::ReadAllText, [System.IO.File]::ReadAllLines, Atlas scripts, subagents, or web search.
- Do not read AGENTS.md, memory files, docs, or any other skill file.
- Use this RoslynKit dev executable for C# inspection: {ROSLYNKIT_PATH}
- Always pass --target .\RoslynKit.slnx for repo symbols.
- Prefer references --symbol with the smallest useful --max-results over broad searches.
- Stop as soon as enough evidence is available.
Return a concise answer with the RoslynKit commands or symbol ids that support it.
'@

    $referencesFlowBaseline = @'
Read-only token benchmark run.
Goal: Trace the full flow for `references --symbol RoslynKit.PositionResolver.GetPositionAsync`, from command registration/parsing through RoslynCommandExecutor.ReferencesAsync, symbol/document resolution, markdown rendering, and relevant test coverage.
Constraints:
- Do not edit files.
- Do not read Codex memory or prior-session artifacts: ${env:USERPROFILE}\.codex\memories, .codex\memories, .codex\sessions, .codex\archived_sessions, history.jsonl, MEMORY.md, rollout_summaries, or rollout-*.jsonl.
- Do not use repo-local memory/cache tools or generated repo-local memory/cache directories.
- Do not use Atlas files or tools: .codex\atlas, atlas-router, atlas-csharp-mapper, or Atlas scripts.
- Do not use subagents such as scout, explorer, or worker.
- Do not use RoslynKit, roslynkit-dev, dotnet run, dotnet test, Atlas scripts, subagents, or web search.
- Use only native PowerShell/text inspection such as Get-ChildItem, Select-String, Get-Content, and ordinary PowerShell.
- Prefer narrow line ranges and stop as soon as enough evidence is available.
Return a concise answer with the inspected evidence paths and the smallest relevant flow.
'@

    $referencesFlowRoslynKit = @'
Read-only token benchmark run.
Goal: Trace the full flow for `references --symbol RoslynKit.PositionResolver.GetPositionAsync`, from command registration/parsing through RoslynCommandExecutor.ReferencesAsync, symbol/document resolution, markdown rendering, and relevant test coverage.
Constraints:
- Do not edit files.
- Do not read Codex memory or prior-session artifacts: ${env:USERPROFILE}\.codex\memories, .codex\memories, .codex\sessions, .codex\archived_sessions, history.jsonl, MEMORY.md, rollout_summaries, or rollout-*.jsonl.
- Do not use repo-local memory/cache tools or generated repo-local memory/cache directories.
- Do not use Atlas files or tools: .codex\atlas, atlas-router, atlas-csharp-mapper, or Atlas scripts.
- Do not use subagents such as scout, explorer, or worker.
- Before C# inspection, read the repo-local RoslynKit dev skill once using exactly this PowerShell command: Get-Content -Raw -LiteralPath '{ROSLYNKIT_SKILL_PATH}'
- Follow that skill's command and token-discipline guidance.
- Apart from that one skill read, do not use rg, Get-Content, Select-String, cat, type, grep, more, [System.IO.File]::ReadAllText, [System.IO.File]::ReadAllLines, Atlas scripts, subagents, or web search.
- Do not read AGENTS.md, memory files, docs, or any other skill file.
- Use this RoslynKit dev executable for C# inspection: {ROSLYNKIT_PATH}
- Always pass --target .\RoslynKit.slnx for repo symbols.
- Use `references --symbol RoslynKit.PositionResolver.GetPositionAsync --max-results 5` first.
- Use `definition --symbol` for known flow symbols, then `document-lines` on the returned path and nearby line window for evidence.
- Prefer `document-lines` over `symbol-source`; do not use `document-text`.
- Do not run broad `symbols` queries. For test coverage only, one narrow `symbols --query References --kind method --max-results 10` command may run, followed by `document-lines` around the relevant test methods.
- Keep the whole investigation to 16 commands or fewer, including the required skill read.
- Stop as soon as enough evidence is available.
Return a concise answer with the RoslynKit commands or symbol ids that support it.
'@

    $cases = @(
        [pscustomobject]@{
            Id             = "fixture-symbol"
            Set            = "fixture"
            BaselinePrompt = $fixtureBaseline
            RoslynKitPrompt = $fixtureRoslynKit
        },
        [pscustomobject]@{
            Id             = "repo-dispatch"
            Set            = "repo"
            BaselinePrompt = $dispatchBaseline
            RoslynKitPrompt = $dispatchRoslynKit
        },
        [pscustomobject]@{
            Id             = "repo-references"
            Set            = "repo"
            BaselinePrompt = $referencesBaseline
            RoslynKitPrompt = $referencesRoslynKit
        },
        [pscustomobject]@{
            Id             = "repo-references-flow"
            Set            = "repo"
            BaselinePrompt = $referencesFlowBaseline
            RoslynKitPrompt = $referencesFlowRoslynKit
            ReferenceBaselineArtifact = "artifacts\token-efficiency\20260704-120341"
            ReferenceBaselineInputTokens = 642575
            ReferenceBaselineUncachedInputTokens = 93199
        }
    )

    foreach ($case in $cases) {
        $case.BaselinePrompt = $case.BaselinePrompt.Trim().Replace("{REPO_ROOT}", $RepoRoot)
        $case.RoslynKitPrompt = $case.RoslynKitPrompt.Trim().Replace("{ROSLYNKIT_PATH}", $RoslynKitPath).Replace("{ROSLYNKIT_SKILL_PATH}", $RoslynKitSkillPath).Replace("{REPO_ROOT}", $RepoRoot)
    }

    return $cases
}

function Get-RunOrder {
    param([int] $Trial)

    if (($Trial % 2) -eq 1) {
        return @("baseline", "roslynkit")
    }

    return @("roslynkit", "baseline")
}

function Invoke-CodexBenchmarkRun {
    param(
        [string] $RepoRoot,
        [string] $RunRoot,
        [object] $Case,
        [string] $Arm,
        [int] $Trial,
        [string] $Model,
        [string] $RoslynKitSkillPath
    )

    $answersDir = Join-Path $RunRoot "answers"
    $eventsDir = Join-Path $RunRoot "events"
    $stderrDir = Join-Path $RunRoot "stderr"
    $commandsDir = Join-Path $RunRoot "commands"
    foreach ($directory in @($answersDir, $eventsDir, $stderrDir, $commandsDir)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $runId = "{0}-{1}-trial{2}" -f $Case.Id, $Arm, $Trial
    $answerPath = Join-Path $answersDir "$runId.md"
    $stdoutPath = Join-Path $eventsDir "$runId.jsonl"
    $stderrPath = Join-Path $stderrDir "$runId.stderr.txt"
    $commandsPath = Join-Path $commandsDir "$runId.txt"
    $prompt = if ($Arm -eq "baseline") { $Case.BaselinePrompt } else { $Case.RoslynKitPrompt }

    $before = Get-RolloutSnapshot
    $startedAtUtc = [DateTime]::UtcNow
    $startedAt = Get-Date

    $args = @()
    if (-not [string]::IsNullOrWhiteSpace($Model)) {
        $args += @("--model", $Model)
    }

    $args += @(
        "--dangerously-bypass-approvals-and-sandbox",
        "exec",
        "--json",
        "--cd", $RepoRoot,
        "--output-last-message", $answerPath
    )

    $args += $prompt

    Write-Host ("Running {0} {1} trial {2}..." -f $Case.Id, $Arm, $Trial)
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & codex @args > $stdoutPath 2> $stderrPath
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $completedAt = Get-Date
    $duration = ($completedAt - $startedAt).TotalSeconds
    $rolloutPath = Find-NewRolloutFile -Before $before -StartedAtUtc $startedAtUtc
    $parsePaths = @($stdoutPath)
    if (-not [string]::IsNullOrWhiteSpace($rolloutPath)) {
        $parsePaths += $rolloutPath
    }

    $tokenUsage = Get-LastTokenUsage -Paths $parsePaths
    $commands = @(Get-CodexShellCommands -Paths $parsePaths | Select-Object -Unique)
    $commands | Set-Content -LiteralPath $commandsPath -Encoding UTF8
    $violations = @(Get-CommandViolations -Arm $Arm -Commands $commands -RoslynKitSkillPath $RoslynKitSkillPath)
    $externalToolViolations = @(Get-ForbiddenExternalToolViolations -Paths $parsePaths)
    $semanticFailures = @(Get-RoslynKitSemanticCommandFailures -Paths $parsePaths)
    $answerFailures = @(Get-AnswerFailureSignals -AnswerPath $answerPath)
    $issues = @($violations + $externalToolViolations + $semanticFailures + $answerFailures)

    $inputTokens = $null
    $cachedInputTokens = $null
    $outputTokens = $null
    $reasoningOutputTokens = $null
    $totalTokens = $null
    if ($null -ne $tokenUsage) {
        $inputTokens = [long] $tokenUsage.input_tokens
        $cachedInputTokens = [long] $tokenUsage.cached_input_tokens
        $outputTokens = [long] $tokenUsage.output_tokens
        $reasoningOutputTokens = [long] $tokenUsage.reasoning_output_tokens
        $totalTokens = [long] $tokenUsage.total_tokens
    }

    $uncachedInputTokens = $null
    if ($null -ne $inputTokens -and $null -ne $cachedInputTokens) {
        $uncachedInputTokens = $inputTokens - $cachedInputTokens
    }

    $valid = ($exitCode -eq 0 -and $null -ne $tokenUsage -and $issues.Count -eq 0)

    return [pscustomobject]@{
        timestamp_utc            = [DateTime]::UtcNow.ToString("o")
        case_id                  = $Case.Id
        case_set                 = $Case.Set
        arm                      = $Arm
        trial                    = $Trial
        model                    = $Model
        valid                    = $valid
        exit_code                = $exitCode
        duration_seconds         = [Math]::Round($duration, 3)
        input_tokens             = $inputTokens
        cached_input_tokens      = $cachedInputTokens
        uncached_input_tokens    = $uncachedInputTokens
        output_tokens            = $outputTokens
        reasoning_output_tokens  = $reasoningOutputTokens
        total_tokens             = $totalTokens
        reference_baseline_input_tokens = Get-ObjectPropertyValue -Object $Case -Name "ReferenceBaselineInputTokens"
        reference_baseline_uncached_input_tokens = Get-ObjectPropertyValue -Object $Case -Name "ReferenceBaselineUncachedInputTokens"
        reference_baseline_artifact = Get-ObjectPropertyValue -Object $Case -Name "ReferenceBaselineArtifact"
        command_count            = $commands.Count
        violation_count          = $issues.Count
        violations               = ($issues -join " | ")
        session_path             = $rolloutPath
        stdout_jsonl_path        = $stdoutPath
        stderr_path              = $stderrPath
        answer_path              = $answerPath
        commands_path            = $commandsPath
    }
}

function Get-Median {
    param([long[]] $Values)

    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) {
        return $null
    }

    $middle = [int] [Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double] $sorted[$middle]
    }

    return (([double] $sorted[$middle - 1]) + ([double] $sorted[$middle])) / 2.0
}

function Format-NullableNumber {
    param($Value)

    if ($null -eq $Value) {
        return ""
    }

    return ([double] $Value).ToString("0.##")
}

function Get-ObjectPropertyValue {
    param(
        [object] $Object,
        [string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Write-Summary {
    param(
        [string] $RunRoot,
        [object[]] $Rows
    )

    $summaryPath = Join-Path $RunRoot "summary.md"
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Codex Token Efficiency Benchmark")
    $lines.Add("")
    $lines.Add("Generated: $((Get-Date).ToString("u"))")
    $lines.Add("")
    $lines.Add('Primary metric: final cumulative `input_tokens` from the last Codex `token_count` event.')
    $lines.Add("")
    $lines.Add("## By Case And Arm")
    $lines.Add("")
    $lines.Add("| Case | Arm | Valid runs | Median input | Mean input | Median uncached input | Mean uncached input | Median commands |")
    $lines.Add("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |")

    $validRows = @($Rows | Where-Object { $_.valid -eq $true -and $null -ne $_.input_tokens })
    foreach ($group in $validRows | Group-Object case_id, arm | Sort-Object Name) {
        $items = @($group.Group)
        $inputValues = @($items | ForEach-Object { [long] $_.input_tokens })
        $uncachedValues = @($items | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { [long] $_.uncached_input_tokens })
        $commandValues = @($items | ForEach-Object { [long] $_.command_count })
        $meanInput = ($inputValues | Measure-Object -Average).Average
        $meanUncached = if ($uncachedValues.Count -gt 0) { ($uncachedValues | Measure-Object -Average).Average } else { $null }
        $parts = $group.Name -split ", "
        $lines.Add(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |" -f
                $parts[0],
                $parts[1],
                $items.Count,
                (Format-NullableNumber (Get-Median -Values $inputValues)),
                (Format-NullableNumber $meanInput),
                (Format-NullableNumber (Get-Median -Values $uncachedValues)),
                (Format-NullableNumber $meanUncached),
                (Format-NullableNumber (Get-Median -Values $commandValues))))
    }

    $lines.Add("")
    $lines.Add("## Savings")
    $lines.Add("")
    $lines.Add("| Case | Baseline median input | RoslynKit median input | Savings % | Baseline median uncached | RoslynKit median uncached | Uncached savings % |")
    $lines.Add("| --- | ---: | ---: | ---: | ---: | ---: | ---: |")

    foreach ($caseGroup in $validRows | Group-Object case_id | Sort-Object Name) {
        $baseline = @($caseGroup.Group | Where-Object { $_.arm -eq "baseline" })
        $roslynkit = @($caseGroup.Group | Where-Object { $_.arm -eq "roslynkit" })
        if ($baseline.Count -eq 0 -or $roslynkit.Count -eq 0) {
            continue
        }

        $baselineMedian = Get-Median -Values @($baseline | ForEach-Object { [long] $_.input_tokens })
        $roslynMedian = Get-Median -Values @($roslynkit | ForEach-Object { [long] $_.input_tokens })
        $baselineUncached = Get-Median -Values @($baseline | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { [long] $_.uncached_input_tokens })
        $roslynUncached = Get-Median -Values @($roslynkit | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { [long] $_.uncached_input_tokens })

        $savings = $null
        if ($baselineMedian -gt 0) {
            $savings = 100.0 * ($baselineMedian - $roslynMedian) / $baselineMedian
        }

        $uncachedSavings = $null
        if ($baselineUncached -gt 0) {
            $uncachedSavings = 100.0 * ($baselineUncached - $roslynUncached) / $baselineUncached
        }

        $lines.Add(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} |" -f
                $caseGroup.Name,
                (Format-NullableNumber $baselineMedian),
                (Format-NullableNumber $roslynMedian),
                (Format-NullableNumber $savings),
                (Format-NullableNumber $baselineUncached),
                (Format-NullableNumber $roslynUncached),
                (Format-NullableNumber $uncachedSavings)))
    }

    $targetRows = @($validRows | Where-Object { $_.arm -eq "roslynkit" -and $null -ne $_.reference_baseline_input_tokens })
    if ($targetRows.Count -gt 0) {
        $lines.Add("")
        $lines.Add("## Reference Baseline Targets")
        $lines.Add("")
        $lines.Add("| Case | Reference artifact | Target input | RoslynKit median input | Input delta | Savings % | Target uncached | RoslynKit median uncached | Uncached delta | Status |")
        $lines.Add("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |")

        foreach ($caseGroup in $targetRows | Group-Object case_id | Sort-Object Name) {
            $items = @($caseGroup.Group)
            $first = $items[0]
            $targetInput = [long] $first.reference_baseline_input_tokens
            $targetUncached = if ($null -ne $first.reference_baseline_uncached_input_tokens) { [long] $first.reference_baseline_uncached_input_tokens } else { $null }
            $artifact = if ($null -ne $first.reference_baseline_artifact) { [string] $first.reference_baseline_artifact } else { "" }
            $roslynMedian = Get-Median -Values @($items | ForEach-Object { [long] $_.input_tokens })
            $roslynUncached = Get-Median -Values @($items | Where-Object { $null -ne $_.uncached_input_tokens } | ForEach-Object { [long] $_.uncached_input_tokens })
            $delta = $targetInput - $roslynMedian
            $savings = if ($targetInput -gt 0) { 100.0 * $delta / $targetInput } else { $null }
            $uncachedDelta = if ($null -ne $targetUncached -and $null -ne $roslynUncached) { $targetUncached - $roslynUncached } else { $null }
            $status = if ($delta -gt 0) { "pass" } else { "fail" }

            $lines.Add(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |" -f
                    $caseGroup.Name,
                    $artifact,
                    (Format-NullableNumber $targetInput),
                    (Format-NullableNumber $roslynMedian),
                    (Format-NullableNumber $delta),
                    (Format-NullableNumber $savings),
                    (Format-NullableNumber $targetUncached),
                    (Format-NullableNumber $roslynUncached),
                    (Format-NullableNumber $uncachedDelta),
                    $status))
        }
    }

    $invalidRows = @($Rows | Where-Object { $_.valid -ne $true })
    $lines.Add("")
    $lines.Add("## Invalid Or Incomplete Runs")
    $lines.Add("")
    if ($invalidRows.Count -eq 0) {
        $lines.Add("None.")
    }
    else {
        $lines.Add("| Case | Arm | Trial | Exit | Violations | Token data |")
        $lines.Add("| --- | --- | ---: | ---: | --- | --- |")
        foreach ($row in $invalidRows) {
            $hasTokenData = if ($null -ne $row.input_tokens) { "yes" } else { "no" }
            $lines.Add(("| {0} | {1} | {2} | {3} | {4} | {5} |" -f
                    $row.case_id,
                    $row.arm,
                    $row.trial,
                    $row.exit_code,
                    (($row.violations -replace "\|", "/")),
                    $hasTokenData))
        }
    }

    $lines | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

function Write-Violations {
    param(
        [string] $RunRoot,
        [object[]] $Rows
    )

    $path = Join-Path $RunRoot "violations.md"
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Benchmark Violations")
    $lines.Add("")
    $invalid = @($Rows | Where-Object { $_.violation_count -gt 0 })
    if ($invalid.Count -eq 0) {
        $lines.Add("No command-policy violations detected.")
    }
    else {
        foreach ($row in $invalid) {
            $lines.Add("## $($row.case_id) $($row.arm) trial $($row.trial)")
            $lines.Add("")
            $lines.Add($row.violations)
            $lines.Add("")
            $lines.Add(('Commands: `{0}`' -f $row.commands_path))
            $lines.Add("")
        }
    }

    $lines | Set-Content -LiteralPath $path -Encoding UTF8
}

$repoRoot = Resolve-RepoRoot
$resolvedRoslynKitPath = Resolve-RoslynKitPath -Candidate $RoslynKitPath
$resolvedRoslynKitSkillPath = Resolve-RoslynKitSkillPath -RepoRoot $repoRoot -Candidate $RoslynKitSkillPath
if (-not (Test-Path -LiteralPath $resolvedRoslynKitSkillPath -PathType Leaf)) {
    throw "RoslynKit dev skill file was not found at '$resolvedRoslynKitSkillPath'. Pass -RoslynKitSkillPath or restore .agents\skills\roslynkit-dev\SKILL.md."
}

if (-not $DryRun -and -not (Test-RoslynKitExecutable -CommandOrPath $resolvedRoslynKitPath)) {
    throw "RoslynKit dev tool was not found at '$resolvedRoslynKitPath'. Pass -RoslynKitPath or install roslynkit-dev."
}

$promptRoslynKitPath = ConvertTo-BenchmarkPromptPath -Path $resolvedRoslynKitPath -RepoRoot $repoRoot
$promptRoslynKitSkillPath = ConvertTo-BenchmarkPromptPath -Path $resolvedRoslynKitSkillPath -RepoRoot $repoRoot

$cases = @(Get-BenchmarkCases -RepoRoot $repoRoot -RoslynKitPath $promptRoslynKitPath -RoslynKitSkillPath $promptRoslynKitSkillPath)
if ($BenchmarkSet -ne "all") {
    $cases = @($cases | Where-Object { $_.Set -eq $BenchmarkSet })
}

if ($CaseId -ne "all") {
    $cases = @($cases | Where-Object { $_.Id -eq $CaseId })
}

if ($cases.Count -eq 0) {
    throw "No benchmark cases matched BenchmarkSet='$BenchmarkSet' CaseId='$CaseId'."
}

if ($DryRun) {
    Write-Host "Dry run only. No Codex sessions will be started."
    Write-Host "Repo: $repoRoot"
    Write-Host "RoslynKit: $promptRoslynKitPath"
    Write-Host "RoslynKit skill: $promptRoslynKitSkillPath"
    foreach ($trial in 1..$Trials) {
        foreach ($case in $cases) {
            foreach ($arm in Get-RunOrder -Trial $trial) {
                $prompt = if ($arm -eq "baseline") { $case.BaselinePrompt } else { $case.RoslynKitPrompt }
                $args = @(
                    "codex"
                )
                if (-not [string]::IsNullOrWhiteSpace($Model)) {
                    $args += @("--model", $Model)
                }
                $args += @(
                    "--dangerously-bypass-approvals-and-sandbox",
                    "exec", "--json", "--cd", $repoRoot,
                    "--output-last-message", "<artifacts answer path>"
                )
                Write-Host ""
                Write-Host ("[{0}] {1} {2} trial {3}" -f $case.Set, $case.Id, $arm, $trial)
                Write-Host ($args -join " ")
                Write-Host "Prompt:"
                Write-Host $prompt
            }
        }
    }

    return
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path (Join-Path $repoRoot "artifacts") (Join-Path "token-efficiency" $timestamp)
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

$rows = New-Object System.Collections.Generic.List[object]
foreach ($trial in 1..$Trials) {
    foreach ($case in $cases) {
        foreach ($arm in Get-RunOrder -Trial $trial) {
            $row = Invoke-CodexBenchmarkRun -RepoRoot $repoRoot -RunRoot $runRoot -Case $case -Arm $arm -Trial $trial -Model $Model -RoslynKitSkillPath $resolvedRoslynKitSkillPath
            $rows.Add($row)
            $rows | Export-Csv -LiteralPath (Join-Path $runRoot "runs.csv") -NoTypeInformation
            Write-Summary -RunRoot $runRoot -Rows $rows
            Write-Violations -RunRoot $runRoot -Rows $rows
        }
    }
}

$rows | Export-Csv -LiteralPath (Join-Path $runRoot "runs.csv") -NoTypeInformation
Write-Summary -RunRoot $runRoot -Rows $rows
Write-Violations -RunRoot $runRoot -Rows $rows

Write-Host ""
Write-Host "Benchmark complete."
Write-Host "Results: $runRoot"
Write-Host "Summary: $(Join-Path $runRoot 'summary.md')"
