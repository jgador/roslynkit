[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Model = "gpt-5.6-sol",
    [ValidateNotNullOrEmpty()]
    [string] $ReasoningEffort = "high",
    [ValidateRange(1, 100)]
    [int] $Trials = 1,
    [string] $CaseId = "all",
    [ValidateNotNullOrEmpty()]
    [string] $IndexPath = "./artifacts/roslynkit.db",
    [string] $ReportRunRoot,
    [switch] $DryRun,
    [Parameter(DontShow)]
    [string] $InternalToolProbePath
)
$ErrorActionPreference = "Stop"
$RoslynKitShellTimeoutMilliseconds = 120000
$MaximumRoslynKitInvocations = 8
$LongContextInputTokenThreshold = 272000
$Gpt56StandardPricing = [ordered]@{
    "gpt-5.6-sol" = [pscustomobject]@{
        short_input = 5.00; short_cached_input = 0.50; short_cache_write = 6.25; short_output = 30.00
        long_input = 10.00; long_cached_input = 1.00; long_cache_write = 12.50; long_output = 45.00
    }
    "gpt-5.6-terra" = [pscustomobject]@{
        short_input = 2.00; short_cached_input = 0.20; short_cache_write = 2.50; short_output = 12.00
        long_input = 4.00; long_cached_input = 0.40; long_cache_write = 5.00; long_output = 18.00
    }
    "gpt-5.6-luna" = [pscustomobject]@{
        short_input = 0.20; short_cached_input = 0.02; short_cache_write = 0.25; short_output = 1.20
        long_input = 0.40; long_cached_input = 0.04; long_cache_write = 0.50; long_output = 1.80
    }
}
$Gpt56PricingSource = "https://developers.openai.com/api/docs/pricing"
$Gpt56PricingVerifiedDate = "2026-08-21"
function Resolve-RepoRoot {
    $root = & git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) { throw "Run the benchmark from a Git worktree." }
    return (Resolve-Path -LiteralPath $root).Path
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
function Resolve-BenchmarkIndexPath {
    param([string] $RepoRoot, [string] $Path)
    $normalized = $Path.Replace("\\", "/")
    if (-not $normalized.StartsWith("./", [System.StringComparison]::Ordinal)) {
        $normalized = "./$normalized"
    }
    if ($normalized -notmatch '^\./artifacts/[A-Za-z0-9._-]+\.db$') {
        throw "IndexPath must be one repository-local database file below ./artifacts/."
    }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $RepoRoot $normalized.Substring(2)))
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "artifacts"))
    if (-not $fullPath.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "IndexPath must remain below the repository artifacts directory."
    }
    return $normalized
}
function Resolve-BenchmarkReportRunRoot {
    param([string] $RepoRoot, [string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "ReportRunRoot must not be empty." }
    $fullPath = if ([IO.Path]::IsPathRooted($Path)) { [IO.Path]::GetFullPath($Path) } else { [IO.Path]::GetFullPath((Join-Path $RepoRoot $Path)) }
    $benchmarkArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "artifacts/codex-benchmark"))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $fullPath.StartsWith($benchmarkArtifactsRoot + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw "ReportRunRoot must identify one run below the repository artifacts/codex-benchmark directory."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $fullPath "runs.json") -PathType Leaf)) {
        throw "ReportRunRoot does not contain runs.json: $fullPath"
    }
    return $fullPath
}
function New-ConditionPrompt {
    param([string] $Condition, [string] $Prompt, [string] $IndexPath = "./artifacts/roslynkit.db")
    $rules = @(
        "Inspection-only benchmark condition: $Condition.",
        "As the first command, run exactly: pwsh -NoProfile -Command `"Get-Content -Raw -LiteralPath '.agents/skills/benchmark/SKILL.md'`". This reads the benchmark skill before investigating code.",
        "The shell is host-dependent and can be Bash on WSL. Never issue a bare PowerShell cmdlet; invoke PowerShell cmdlets through pwsh -NoProfile -Command.",
        "Do not edit files or change Git state.",
        "Do not run builds, restores, tests, or other commands that write caches; inspect test source instead.",
        "Do not use web search, browsers, network requests, or subagents. Do not inspect memory, prior-session files, Atlas, CODEX_HOME, .codex, AGENTS.md, or agent context not explicitly permitted here.",
        "Do not inspect the benchmark controller, private benchmark data, prior benchmark artifacts, or benchmark procedure documentation.",
        "Do not run repository-root recursive searches. Scope every recursive or literal search to explicit permitted source or test paths.",
        "Do not use ``rg --files``; name known source or test paths explicitly and use bounded literal searches only.",
        "Use only simple inspection commands that do not modify the repository and are expected to succeed. A declined command or nonzero exit code invalidates the run.",
        "Return concise source-and-test evidence; do not change files."
    )
    if ($Condition -eq "raw-codex") {
        $rules += "Do not read .agents/skills/roslynkit or any file below it."
        $rules += "Use ordinary local shell and text inspection only. Do not invoke RoslynKit, roslynkit-dev, or dotnet run for RoslynKit."
        $rules += "Use only the known source root src/RoslynKit and test root tests/RoslynKit.Tests. Do not use find, enumerate directories, or probe speculative paths such as test or tests. For rg searches, scope to those known directories instead of listing guessed filenames; only read a specific file after a prior command emitted that path."
    }
    else {
        $rules += "Then run exactly: pwsh -NoProfile -Command `"Get-Content -Raw -LiteralPath '.agents/skills/roslynkit/SKILL.md'; Get-Content -Raw -LiteralPath '.agents/skills/roslynkit/references/commands.md'; Get-Content -Raw -LiteralPath '.agents/skills/roslynkit/references/output.md'`". This reads the stable skill and its command and output references before invoking RoslynKit."
        $rules += "Invoke the global RoslynKit from PATH as 'roslynkit' for code investigation."
        $rules += "Pass --target ./RoslynKit.slnx to RoslynKit. The prepared repository-local search index is $IndexPath; pass --index-path $IndexPath to search."
        $rules += "Set timeout_ms to $RoslynKitShellTimeoutMilliseconds on every shell tool call that invokes RoslynKit; the shell tool's default deadline is too short for a cold workspace command."
        $rules += "Run only one RoslynKit command at a time and wait for it to finish before starting another. Do not use concurrent tool calls, background jobs, or parallel pipelines for RoslynKit."
        $rules += "Use at most $MaximumRoslynKitInvocations RoslynKit invocations total, including search and source or test reads."
        $rules += "Start intent discovery with one narrow roslynkit search query and --max-results 10. If it returns no useful method or location, run one refined search with --max-results 10 and add --kind method when appropriate."
        $rules += "Only if the refined results still lack a reliable jump target, run one third and final search with --max-results 20; use --max-results 50 instead only when the earlier rankings show many plausible near-ties. Never run a fourth search. Prefer bounded source slices over whole-file output."
        $rules += "Before investigating, turn every requested behavior, numeric limit, timing rule, and failure or reuse branch into an evidence checklist. Do not answer until each clause is supported by an emitted implementation or focused-test location."
        $rules += "Treat every id: selector as opaque and copy it verbatim. When an id contains PowerShell backticks, either pass it as one single-quoted --symbol value or use its returned loc with a bounded document-lines call; never reconstruct or rewrite the id."
        $rules += "Never guess or shorten a RoslynKit selector. For definition, references, implementations, and symbol-source, use an exact N:, T:, M:, P:, F:, or E: id emitted by a successful RoslynKit command; if no exact id is available, search first or use the returned loc with a bounded file slice. Never substitute the adjacent display name for an emitted id."
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
    $requested = @("apps", "browser_use", "browser_use_external", "browser_use_full_cdp_access", "computer_use", "external_agent_memory_import", "goals", "hooks", "image_generation", "in_app_browser", "memories", "multi_agent", "multi_agent_v2", "plugin_sharing", "plugins", "remote_plugin", "shell_snapshot", "skill_mcp_dependency_install", "skill_search", "standalone_web_search", "unified_exec", "workspace_dependencies")
    if ($DryRunMode) { return $requested }
    $featureLines = @(& codex features list)
    if ($LASTEXITCODE -ne 0) { throw "The installed Codex CLI could not enumerate features for benchmark isolation." }
    $available = @($featureLines | ForEach-Object { ($_ -split '\s+')[0] })
    return Select-DisabledFeatures -Requested $requested -Available $available
}
function Select-DisabledFeatures {
    param([string[]] $Requested, [string[]] $Available)
    return @($Requested | Where-Object { $_ -eq "unified_exec" -or $Available -contains $_ })
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
    param([string] $RepoRoot, [string] $ResolvedRoslynKitPath, [string] $IndexPath)
    Push-Location $RepoRoot
    try {
        New-Item -ItemType Directory -Force -Path "./artifacts" | Out-Null
        & $ResolvedRoslynKitPath index --target "./RoslynKit.slnx" --index-path $IndexPath
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
    if ([string]::IsNullOrWhiteSpace($RepoRoot) -or [string]::IsNullOrWhiteSpace($ResolvedRoslynKitPath) -or
        -not (Test-Path -LiteralPath (Join-Path $RepoRoot "RoslynKit.slnx") -PathType Leaf)) {
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
function Set-HostCodexHome {
    $codexHome = if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        $env:CODEX_HOME
    }
    else {
        Get-DefaultCodexHome
    }
    if (-not (Test-Path -LiteralPath $codexHome -PathType Container)) {
        throw "The active host CODEX_HOME directory was not found: '$codexHome'."
    }
    $resolvedHome = (Resolve-Path -LiteralPath $codexHome).Path
    $configPath = Join-Path $resolvedHome "config.toml"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "The active host Codex configuration was not found: '$configPath'."
    }
    [Environment]::SetEnvironmentVariable("CODEX_HOME", $resolvedHome, "Process")
    return $configPath
}
function Read-EventLog {
    param([string] $Path)
    $events = New-Object System.Collections.Generic.List[object]
    $issues = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $issues.Add("event log was not written")
        return [pscustomobject]@{ events = @(); issues = $issues.ToArray() }
    }
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $Path) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $events.Add(($line | ConvertFrom-Json))
        }
        catch {
            $issues.Add("event log line $lineNumber was not valid JSON")
        }
    }
    return [pscustomobject]@{ events = $events.ToArray(); issues = $issues.ToArray() }
}
function Read-Events {
    param([string] $Path)
    return (Read-EventLog -Path $Path).events
}
function ConvertTo-TokenCount {
    param([object] $Usage, [string] $Name, [bool] $Required, [System.Collections.Generic.List[string]] $Issues)
    $value = Get-ObjectPropertyValue -InputObject $Usage -Name $Name
    if ($null -eq $value) {
        if ($Required) { $Issues.Add("usage omitted $Name") }
        return $null
    }
    $parsed = 0L
    if (-not [long]::TryParse(
            [string] $value,
            [Globalization.NumberStyles]::Integer,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref] $parsed) -or $parsed -lt 0) {
        $Issues.Add("usage field $Name was not a nonnegative integer")
        return $null
    }
    return $parsed
}
function Get-TokenAccounting {
    param([object[]] $Events)
    $issues = New-Object System.Collections.Generic.List[string]
    $terminalEvents = @($Events | Where-Object {
            $_.type -eq "turn.completed" -and $null -ne (Get-ObjectPropertyValue -InputObject $_ -Name "usage")
        })
    $legacyEvents = @($Events | Where-Object {
            $payload = Get-ObjectPropertyValue -InputObject $_ -Name "payload"
            $info = Get-ObjectPropertyValue -InputObject $payload -Name "info"
            (Get-ObjectPropertyValue -InputObject $payload -Name "type") -eq "token_count" -and
            $null -ne (Get-ObjectPropertyValue -InputObject $info -Name "total_token_usage")
        })
    $usage = $null
    $usageSource = $null
    if ($terminalEvents.Count -eq 1) {
        $usage = Get-ObjectPropertyValue -InputObject $terminalEvents[0] -Name "usage"
        $usageSource = "turn.completed.usage"
    }
    elseif ($terminalEvents.Count -gt 1) {
        $issues.Add("event log contained $($terminalEvents.Count) terminal usage events for one ephemeral Codex exec turn")
    }
    elseif ($legacyEvents.Count -gt 0) {
        $lastLegacyPayload = Get-ObjectPropertyValue -InputObject $legacyEvents[-1] -Name "payload"
        $lastLegacyInfo = Get-ObjectPropertyValue -InputObject $lastLegacyPayload -Name "info"
        $usage = Get-ObjectPropertyValue -InputObject $lastLegacyInfo -Name "total_token_usage"
        $usageSource = "token_count.info.total_token_usage"
    }
    else {
        $issues.Add("event log did not contain terminal token accounting")
    }
    if ($null -eq $usage) {
        return [pscustomobject]@{
            usage = $null; issues = $issues.ToArray(); usage_source = $usageSource
            usage_scope = "completed_turn_aggregate"; terminal_usage_event_count = $terminalEvents.Count
            request_usage_available = $false; max_request_input_tokens = $null; requests_over_long_context_threshold = $null
        }
    }
    $inputTokens = ConvertTo-TokenCount -Usage $usage -Name "input_tokens" -Required $true -Issues $issues
    $cachedInputTokens = ConvertTo-TokenCount -Usage $usage -Name "cached_input_tokens" -Required $true -Issues $issues
    $cacheWriteInputTokens = ConvertTo-TokenCount -Usage $usage -Name "cache_write_input_tokens" -Required $false -Issues $issues
    $alternateCacheWriteTokens = ConvertTo-TokenCount -Usage $usage -Name "cache_write_tokens" -Required $false -Issues $issues
    if ($null -eq $cacheWriteInputTokens) {
        $cacheWriteInputTokens = $alternateCacheWriteTokens
    }
    elseif ($null -ne $alternateCacheWriteTokens -and $alternateCacheWriteTokens -ne $cacheWriteInputTokens) {
        $issues.Add("usage cache-write token aliases disagreed")
    }
    $outputTokens = ConvertTo-TokenCount -Usage $usage -Name "output_tokens" -Required $true -Issues $issues
    $reasoningOutputTokens = ConvertTo-TokenCount -Usage $usage -Name "reasoning_output_tokens" -Required $true -Issues $issues
    $uncachedInputTokens = $null
    $regularUncachedInputTokens = $null
    if ($null -ne $inputTokens -and $null -ne $cachedInputTokens) {
        if ($cachedInputTokens -gt $inputTokens) {
            $issues.Add("cached_input_tokens exceeded input_tokens")
        }
        else {
            $uncachedInputTokens = $inputTokens - $cachedInputTokens
            if ($null -ne $cacheWriteInputTokens) {
                if ($cacheWriteInputTokens -gt $uncachedInputTokens) {
                    $issues.Add("cache_write_input_tokens exceeded non-cached input tokens")
                }
                else {
                    $regularUncachedInputTokens = $uncachedInputTokens - $cacheWriteInputTokens
                }
            }
        }
    }
    $normalizedUsage = [pscustomobject]@{
        input_tokens = $inputTokens
        cached_input_tokens = $cachedInputTokens
        cache_write_input_tokens = $cacheWriteInputTokens
        uncached_input_tokens = $uncachedInputTokens
        regular_uncached_input_tokens = $regularUncachedInputTokens
        output_tokens = $outputTokens
        reasoning_output_tokens = $reasoningOutputTokens
    }
    return [pscustomobject]@{
        usage = $normalizedUsage; issues = $issues.ToArray(); usage_source = $usageSource
        usage_scope = "completed_turn_aggregate"; terminal_usage_event_count = $terminalEvents.Count
        request_usage_available = $false; max_request_input_tokens = $null; requests_over_long_context_threshold = $null
    }
}
function Get-TokenUsage {
    param([object[]] $Events)
    return (Get-TokenAccounting -Events $Events).usage
}
function Get-Gpt56Pricing {
    param([string] $Model)
    $resolvedModel = if ($Model -eq "gpt-5.6") { "gpt-5.6-sol" } else { $Model }
    if (-not $Gpt56StandardPricing.Contains($resolvedModel)) { return $null }
    return [pscustomobject]@{ model = $resolvedModel; rates = $Gpt56StandardPricing[$resolvedModel] }
}
function Get-Gpt56CostProjection {
    param([object] $Usage, [string] $Model, [ValidateSet("short", "long")][string] $ContextClass = "short")
    $pricing = Get-Gpt56Pricing -Model $Model
    if ($null -eq $Usage -or $null -eq $pricing) { return $null }
    $regularUncachedInputTokens = Get-ObjectPropertyValue -InputObject $Usage -Name "regular_uncached_input_tokens"
    $uncachedInputTokens = Get-ObjectPropertyValue -InputObject $Usage -Name "uncached_input_tokens"
    $cachedInputTokens = Get-ObjectPropertyValue -InputObject $Usage -Name "cached_input_tokens"
    $cacheWriteInputTokens = Get-ObjectPropertyValue -InputObject $Usage -Name "cache_write_input_tokens"
    $outputTokens = Get-ObjectPropertyValue -InputObject $Usage -Name "output_tokens"
    if ($null -eq $cachedInputTokens -or $null -eq $outputTokens -or
        ($null -eq $regularUncachedInputTokens -and $null -eq $uncachedInputTokens)) {
        return $null
    }
    $rates = $pricing.rates
    $inputRate = [double] (Get-ObjectPropertyValue -InputObject $rates -Name "${ContextClass}_input")
    $cachedInputRate = [double] (Get-ObjectPropertyValue -InputObject $rates -Name "${ContextClass}_cached_input")
    $cacheWriteRate = [double] (Get-ObjectPropertyValue -InputObject $rates -Name "${ContextClass}_cache_write")
    $outputRate = [double] (Get-ObjectPropertyValue -InputObject $rates -Name "${ContextClass}_output")
    $cacheWriteKnown = $null -ne $cacheWriteInputTokens -and $null -ne $regularUncachedInputTokens
    $ordinaryInputTokens = if ($cacheWriteKnown) { [long] $regularUncachedInputTokens } else { [long] $uncachedInputTokens }
    $ordinaryInputCost = [double] $ordinaryInputTokens / 1000000.0 * $inputRate
    $cachedInputCost = [double] $cachedInputTokens / 1000000.0 * $cachedInputRate
    $cacheWriteCost = if ($cacheWriteKnown) { [double] $cacheWriteInputTokens / 1000000.0 * $cacheWriteRate } else { $null }
    $outputCost = [double] $outputTokens / 1000000.0 * $outputRate
    $totalCost = $ordinaryInputCost + $cachedInputCost + $outputCost
    if ($null -ne $cacheWriteCost) { $totalCost += $cacheWriteCost }
    return [pscustomobject]@{
        model = $pricing.model
        context_class = $ContextClass
        regular_uncached_input_cost_usd = [Math]::Round($ordinaryInputCost, 9)
        cached_input_cost_usd = [Math]::Round($cachedInputCost, 9)
        cache_write_cost_usd = if ($null -ne $cacheWriteCost) { [Math]::Round($cacheWriteCost, 9) } else { $null }
        output_cost_usd = [Math]::Round($outputCost, 9)
        total_cost_usd = [Math]::Round($totalCost, 9)
        status = if ($cacheWriteKnown) { "complete" } else { "excluding_cache_write_uplift" }
    }
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
function Remove-CommandEnvelopeQuotes {
    param([string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $Value }
    $trimmed = $Value.Trim()
    if ($trimmed.Length -lt 2) { return $trimmed }
    $openingQuote = $trimmed[0]
    if (($openingQuote -ne '"' -and $openingQuote -ne "'") -or $trimmed[$trimmed.Length - 1] -ne $openingQuote) {
        return $trimmed
    }
    $payload = $trimmed.Substring(1, $trimmed.Length - 2)
    if ($openingQuote -eq '"') {
        return $payload.Replace('\"', '"').Replace('`"', '"')
    }
    return $payload.Replace("''", "'")
}
function Get-TimeoutCommandPayload {
    param([string] $Arguments)
    $remaining = $Arguments.Trim()
    $optionsWithValues = @("-k", "--kill-after", "-s", "--signal")
    while (-not [string]::IsNullOrWhiteSpace($remaining)) {
        $wordMatch = [regex]::Match($remaining, '^(?<word>\S+)(?:\s+(?<rest>[\s\S]*))?$')
        if (-not $wordMatch.Success) { return $null }
        $word = $wordMatch.Groups["word"].Value
        $rest = $wordMatch.Groups["rest"].Value
        if ($word -eq "--") {
            $remaining = $rest
            continue
        }
        if ($word.StartsWith("-", [System.StringComparison]::Ordinal)) {
            if ($word -in $optionsWithValues) {
                $optionValueMatch = [regex]::Match($rest, '^\S+(?:\s+(?<rest>[\s\S]*))?$')
                if (-not $optionValueMatch.Success) { return $null }
                $rest = $optionValueMatch.Groups["rest"].Value
            }
            $remaining = $rest
            continue
        }
        if ($word -notmatch '^(?:\d+(?:\.\d+)?|\.\d+)[smhd]?$' -or [string]::IsNullOrWhiteSpace($rest)) {
            return $null
        }
        return $rest.Trim()
    }
    return $null
}
function Get-ShellEnvelopePayload {
    param([string] $Command)
    if ([string]::IsNullOrWhiteSpace($Command)) { return $null }
    $trimmed = $Command.Trim()
    $headMatch = [regex]::Match($trimmed, '^(?:&\s*)?(?:"(?<double>[^"]+)"|''(?<single>[^'']+)''|(?<bare>[^\s]+))(?:\s+(?<arguments>[\s\S]*))?$')
    if (-not $headMatch.Success) { return $null }
    $executable = if ($headMatch.Groups["double"].Success) {
        $headMatch.Groups["double"].Value
    }
    elseif ($headMatch.Groups["single"].Success) {
        $headMatch.Groups["single"].Value
    }
    else {
        $headMatch.Groups["bare"].Value
    }
    try {
        $commandFile = [IO.Path]::GetFileName($executable).ToLowerInvariant()
    }
    catch {
        return $null
    }
    $arguments = $headMatch.Groups["arguments"].Value
    $payloadMatch = $null
    if ($commandFile -in @("bash", "bash.exe", "sh", "sh.exe", "zsh", "zsh.exe")) {
        $payloadMatch = [regex]::Match($arguments, '(?is)(?:^|\s)-(?:lc|c)\s+(?<payload>.+)$')
    }
    elseif ($commandFile -in @("pwsh", "pwsh.exe", "powershell", "powershell.exe")) {
        $payloadMatch = [regex]::Match($arguments, '(?is)(?:^|\s)-(?:Command|c)\s+(?<payload>.+)$')
    }
    elseif ($commandFile -in @("cmd", "cmd.exe")) {
        $payloadMatch = [regex]::Match($arguments, '(?is)(?:^|\s)/(?:c|k)\s+(?<payload>.+)$')
    }
    elseif ($commandFile -in @("invoke-expression", "iex")) {
        return Remove-CommandEnvelopeQuotes -Value $arguments
    }
    elseif ($commandFile -in @("timeout", "timeout.exe")) {
        return Get-TimeoutCommandPayload -Arguments $arguments
    }
    if ($null -eq $payloadMatch -or -not $payloadMatch.Success) { return $null }
    return Remove-CommandEnvelopeQuotes -Value $payloadMatch.Groups["payload"].Value
}
function Get-NormalizedCommandPayloads {
    param([string] $Command)
    $payloads = New-Object System.Collections.Generic.List[string]
    $current = $Command.Trim()
    foreach ($depth in 0..8) {
        if ([string]::IsNullOrWhiteSpace($current)) { break }
        if (-not $payloads.Contains($current)) { $payloads.Add($current) }
        $next = Get-ShellEnvelopePayload -Command $current
        if ([string]::IsNullOrWhiteSpace($next) -or [string]::Equals($next, $current, [System.StringComparison]::Ordinal)) { break }
        $current = $next.Trim()
    }
    return $payloads.ToArray()
}
function Test-RoslynKitCoreInvocation {
    param([string] $Command, [string] $ResolvedRoslynKitPath)
    if ([string]::IsNullOrWhiteSpace($Command)) { return $false }
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
    $processStartPattern = '(?is)::Start\(\s*["''](?:[^"'']*[\\/])?roslynkit(?:-dev)?(?:\.exe)?["'']'
    return $Command -match $resolvedVariablePattern -or
        $Command -match $dotnetPattern -or
        $Command -match $directResolverInvocationPattern -or
        $Command -match $processStartPattern
}
function Test-RoslynKitInvocation {
    param([string] $Command, [string] $ResolvedRoslynKitPath)
    foreach ($payload in Get-NormalizedCommandPayloads -Command $Command) {
        if (Test-RoslynKitCoreInvocation -Command $payload -ResolvedRoslynKitPath $ResolvedRoslynKitPath) {
            return $true
        }
    }
    return $false
}
function Get-RoslynKitInvocationCount {
    param([string[]] $Commands, [string] $ResolvedRoslynKitPath)
    return @($Commands | Where-Object {
            Test-RoslynKitInvocation -Command $_ -ResolvedRoslynKitPath $ResolvedRoslynKitPath
        }).Count
}
function Get-PatternSearchScopeArguments {
    param(
        [string[]] $Arguments,
        [string[]] $OptionsWithValues,
        [string[]] $PatternOptions,
        [string[]] $ModesWithoutPattern = @()
    )
    $positionals = New-Object System.Collections.Generic.List[string]
    $patternProvided = $false
    $modeWithoutPattern = $false
    $endOfOptions = $false
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        $argument = $Arguments[$index]
        if (-not $endOfOptions -and $argument -eq "--") {
            $endOfOptions = $true
            continue
        }
        if (-not $endOfOptions -and $argument.StartsWith("-", [System.StringComparison]::Ordinal) -and $argument -ne "-") {
            $optionName = ($argument -split "=", 2)[0]
            $hasInlineValue = $argument.Contains("=", [System.StringComparison]::Ordinal)
            $hasAttachedPattern = @($PatternOptions | Where-Object {
                    $_ -match '^-[A-Za-z]$' -and
                    $argument.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase) -and
                    $argument.Length -gt $_.Length
                }).Count -gt 0
            if ($optionName -in $PatternOptions -or $hasAttachedPattern) {
                $patternProvided = $true
            }
            if ($optionName -in $ModesWithoutPattern) {
                $modeWithoutPattern = $true
            }
            if (-not $hasInlineValue -and $optionName -in $OptionsWithValues -and $index + 1 -lt $Arguments.Count) {
                $index++
            }
            continue
        }
        $positionals.Add($argument)
    }
    if ($patternProvided -or $modeWithoutPattern) {
        return $positionals.ToArray()
    }
    if ($positionals.Count -le 1) {
        return @()
    }
    return @($positionals.ToArray() | Select-Object -Skip 1)
}
function Test-IsRepositoryRootScope {
    param([string] $Scope, [string] $RepoRoot)
    $providerQualifierPattern = '(?i)^(?:Microsoft\.PowerShell\.Core\\)?FileSystem::'
    $normalizedScope = ((Remove-CommandEnvelopeQuotes -Value $Scope).Trim() -replace $providerQualifierPattern, '')
    if ($normalizedScope -match '^\.[\\/]*$' -or
        $normalizedScope -match '^\$(?:env:)?PWD(?:\.Path)?(?:[\\/]*)$' -or
        $normalizedScope -match '^\$\{(?:env:)?PWD\}(?:\.Path)?(?:[\\/]*)$' -or
        $normalizedScope -match '^\$\(\s*(?:pwd|Get-Location)\s*\)(?:\.Path)?(?:[\\/]*)$' -or
        $normalizedScope -match '^\(\s*Get-Location\s*\)(?:\.Path)?(?:[\\/]*)$' -or
        $normalizedScope -match '^%CD%(?:[\\/]*)$') {
        return $true
    }
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        return $false
    }
    try {
        $nativeRepoRoot = $RepoRoot -replace $providerQualifierPattern, ''
        $rootPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($nativeRepoRoot))
        $expandedScope = $normalizedScope
        $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        $homeMatch = [regex]::Match($normalizedScope, '(?i)^(?:~|\$(?:env:)?HOME|\$\{(?:env:)?HOME\}|\$env:USERPROFILE|%USERPROFILE%)(?:[\\/](?<relative>.*))?$')
        if ($homeMatch.Success -and -not [string]::IsNullOrWhiteSpace($userProfile)) {
            $relativeHomePath = $homeMatch.Groups["relative"].Value
            $expandedScope = if ([string]::IsNullOrWhiteSpace($relativeHomePath)) {
                $userProfile
            }
            else {
                Join-Path $userProfile $relativeHomePath
            }
        }
        $scopePath = if ([IO.Path]::IsPathRooted($expandedScope)) {
            [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($expandedScope))
        }
        else {
            [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath((Join-Path $rootPath $expandedScope)))
        }
        $comparison = if ($IsWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
        return [string]::Equals($scopePath, $rootPath, $comparison)
    }
    catch {
        return $false
    }
}
function Test-ScopesUseRepositoryRoot {
    param([string[]] $Scopes, [string] $RepoRoot)
    $scopeArray = @($Scopes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($scopeArray.Count -eq 0) {
        return $true
    }
    return @($scopeArray | Where-Object { Test-IsRepositoryRootScope -Scope $_ -RepoRoot $RepoRoot }).Count -gt 0
}
function Get-ChildItemScopeArguments {
    param([string[]] $Arguments)
    $scopes = New-Object System.Collections.Generic.List[string]
    $optionsWithValues = @("-Depth", "-Filter", "-Include", "-Exclude", "-Attributes")
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        $argument = $Arguments[$index]
        if ($argument -match '(?i)^-(?:Path|LiteralPath)(?::(?<value>.+))?$') {
            if ($Matches.value) {
                $scopes.Add($Matches.value)
            }
            elseif ($index + 1 -lt $Arguments.Count) {
                $scopes.Add($Arguments[++$index])
            }
            continue
        }
        $optionName = ($argument -split ":", 2)[0]
        if ($optionName -in $optionsWithValues) {
            if (-not $argument.Contains(":", [System.StringComparison]::Ordinal) -and $index + 1 -lt $Arguments.Count) {
                $index++
            }
            continue
        }
        if ($argument.StartsWith("-", [System.StringComparison]::Ordinal)) { continue }
        $scopes.Add($argument)
    }
    return $scopes.ToArray()
}
function Get-FindScopeArguments {
    param([string[]] $Arguments)
    $scopes = New-Object System.Collections.Generic.List[string]
    foreach ($argument in $Arguments) {
        if ($argument -match '^(?:-|!|\()') { break }
        $scopes.Add($argument)
    }
    return $scopes.ToArray()
}
function Test-RepositoryRootRecursiveSearch {
    param([string] $Command, [string] $RepoRoot = "")
    foreach ($payload in Get-NormalizedCommandPayloads -Command $Command) {
        $trimmedPayload = $payload.Trim()
        $parseInput = if ($trimmedPayload -match '^["'']') { "& $trimmedPayload" } else { $trimmedPayload }
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($parseInput, [ref] $tokens, [ref] $parseErrors)
        $commandAsts = @($ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst]
                }, $true))
        foreach ($commandAst in $commandAsts) {
            $commandName = $commandAst.GetCommandName()
            if ([string]::IsNullOrWhiteSpace($commandName)) { continue }
            try {
                $commandFile = [IO.Path]::GetFileName($commandName).ToLowerInvariant()
            }
            catch {
                continue
            }
            $arguments = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object {
                    (Remove-CommandEnvelopeQuotes -Value $_.Extent.Text).Trim()
                })
            if ($commandFile -in @("rg", "rg.exe", "ripgrep", "ripgrep.exe")) {
                if (@($arguments | Where-Object { $_ -in @("--help", "-h", "--version", "-V", "--type-list", "--pcre2-version") }).Count -gt 0) { continue }
                $scopes = @(Get-PatternSearchScopeArguments -Arguments $arguments `
                        -OptionsWithValues @("-A", "--after-context", "-B", "--before-context", "-C", "--context", "--colors", "--context-separator", "--dfa-size-limit", "-E", "--encoding", "--engine", "-e", "--regexp", "-f", "--file", "--field-context-separator", "--field-match-separator", "-g", "--glob", "--iglob", "--ignore-file", "-j", "--threads", "-M", "--max-columns", "-m", "--max-count", "--max-depth", "--max-filesize", "--path-separator", "--pre", "--pre-glob", "-r", "--replace", "--regex-size-limit", "--sort", "--sortr", "-t", "--type", "-T", "--type-not", "--type-add", "--type-clear") `
                        -PatternOptions @("-e", "--regexp", "-f", "--file") `
                        -ModesWithoutPattern @("--files"))
                if (Test-ScopesUseRepositoryRoot -Scopes $scopes -RepoRoot $RepoRoot) { return $true }
                continue
            }
            if ($commandFile -in @("grep", "grep.exe", "egrep", "egrep.exe", "fgrep", "fgrep.exe") -and
                @($arguments | Where-Object { $_ -eq "--recursive" -or $_ -match '^-[^-]*[rR]' }).Count -gt 0) {
                $scopes = @(Get-PatternSearchScopeArguments -Arguments $arguments `
                        -OptionsWithValues @("-A", "--after-context", "-B", "--before-context", "-C", "--context", "-D", "--devices", "-d", "--directories", "-e", "--regexp", "-f", "--file", "--exclude", "--exclude-from", "--exclude-dir", "--group-separator", "--include", "-m", "--max-count") `
                        -PatternOptions @("-e", "--regexp", "-f", "--file"))
                if (Test-ScopesUseRepositoryRoot -Scopes $scopes -RepoRoot $RepoRoot) { return $true }
                continue
            }
            if ($commandFile -in @("get-childitem", "gci", "dir") -and
                @($arguments | Where-Object { $_ -match '(?i)^-Recurse(?::.*)?$' -or $_ -eq "/s" }).Count -gt 0) {
                $scopes = if (@($arguments | Where-Object { $_ -eq "/s" }).Count -gt 0) {
                    @($arguments | Where-Object { -not $_.StartsWith("/", [System.StringComparison]::Ordinal) })
                }
                else {
                    @(Get-ChildItemScopeArguments -Arguments $arguments)
                }
                if (Test-ScopesUseRepositoryRoot -Scopes $scopes -RepoRoot $RepoRoot) { return $true }
                continue
            }
            if ($commandFile -in @("fd", "fd.exe", "fdfind", "fdfind.exe")) {
                if (@($arguments | Where-Object { $_ -in @("--help", "-h", "--version", "-V") }).Count -gt 0) { continue }
                $scopes = @(Get-PatternSearchScopeArguments -Arguments $arguments `
                        -OptionsWithValues @("-d", "--max-depth", "--min-depth", "--exact-depth", "-E", "--exclude", "-e", "--extension", "-g", "--glob", "-j", "--threads", "--max-buffer-time", "--path-separator", "--search-path", "-t", "--type") `
                        -PatternOptions @())
                if (Test-ScopesUseRepositoryRoot -Scopes $scopes -RepoRoot $RepoRoot) { return $true }
                continue
            }
            if ($commandFile -eq "find" -and @($arguments | Where-Object { $_ -in @("--help", "--version") }).Count -eq 0) {
                $scopes = @(Get-FindScopeArguments -Arguments $arguments)
                if (Test-ScopesUseRepositoryRoot -Scopes $scopes -RepoRoot $RepoRoot) { return $true }
            }
        }
    }
    return $false
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
    param([string] $Command, [string] $ContextPath)
    $pathPattern = Get-ContextPathPattern -ContextPath $ContextPath
    foreach ($payload in Get-NormalizedCommandPayloads -Command $Command) {
        $normalizedPayload = $payload.Replace("\\", "\").Replace("//", "/")
        if (-not [regex]::IsMatch($normalizedPayload, $pathPattern)) { continue }
        $parseInput = if ($normalizedPayload.Trim() -match '^["'']') { "& $normalizedPayload" } else { $normalizedPayload }
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($parseInput, [ref] $tokens, [ref] $parseErrors)
        $commandAsts = @($ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst]
                }, $true))
        foreach ($commandAst in $commandAsts) {
            $commandName = $commandAst.GetCommandName()
            if ([string]::IsNullOrWhiteSpace($commandName)) { continue }
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
        }
    }
    return $false
}
function Test-ForbiddenContextSurface {
    param([string] $Condition, [string] $Command, [bool] $UsesRoslynKit, [string] $RepoRoot = "", [string] $AllowedIndexPath = "./artifacts/roslynkit.db")
    if (Test-RepositoryRootRecursiveSearch -Command $Command -RepoRoot $RepoRoot) {
        return $true
    }
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
        $allowedIndexArgumentPattern = '(?i)--index-path(?:\s+|=)["'']?' + [regex]::Escape($AllowedIndexPath).Replace("/", "[\\/]") + '["'']?'
        $remainingCommand = [regex]::Replace($remainingCommand, $allowedIndexArgumentPattern, '')
    }
    return $remainingCommand -match '(?i)\.agents(?:[\\/]|$)|(?:^|[^A-Za-z0-9_.-])artifacts[\\/]|AGENTS\.md'
}
function Get-ComplianceIssues {
    param([string] $Condition, [string[]] $Commands, [object[]] $Events, [string[]] $RepositoryChanges, [string] $ResolvedRoslynKitPath, [string] $RepoRoot = "", [string] $AllowedIndexPath = "./artifacts/roslynkit.db")
    $issues = New-Object System.Collections.Generic.List[string]
    $roslynKitInvocationCount = Get-RoslynKitInvocationCount -Commands $Commands -ResolvedRoslynKitPath $ResolvedRoslynKitPath
    $observedRoslynKit = $roslynKitInvocationCount -gt 0
    foreach ($command in $Commands) {
        $usesRoslynKit = Test-RoslynKitInvocation -Command $command -ResolvedRoslynKitPath $ResolvedRoslynKitPath
        if ($Condition -eq "raw-codex" -and $usesRoslynKit) {
            $issues.Add("raw-codex invoked RoslynKit: $command")
        }
        if ($command -match "(?i)\b(curl|wget|Invoke-WebRequest|Invoke-RestMethod)\b|https?://") {
            $issues.Add("used web or network access: $command")
        }
        if (Test-ForbiddenContextSurface -Condition $Condition -Command $command -UsesRoslynKit $usesRoslynKit -RepoRoot $RepoRoot -AllowedIndexPath $AllowedIndexPath) {
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
    if ($Condition -eq "roslynkit" -and $roslynKitInvocationCount -gt $MaximumRoslynKitInvocations) {
        $issues.Add("RoslynKit condition used $roslynKitInvocationCount invocations; maximum is $MaximumRoslynKitInvocations")
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
function Get-BenchmarkHostKind {
    if ($IsWindows) { return "windows" }
    $isWsl = -not [string]::IsNullOrWhiteSpace($env:WSL_DISTRO_NAME) -or
        -not [string]::IsNullOrWhiteSpace($env:WSL_INTEROP)
    if (-not $isWsl -and (Test-Path -LiteralPath "/proc/sys/kernel/osrelease" -PathType Leaf)) {
        $isWsl = (Get-Content -Raw -LiteralPath "/proc/sys/kernel/osrelease") -match '(?i)microsoft|wsl'
    }
    if ($isWsl) {
        $isVsCodeRemote = [string]::Equals($env:TERM_PROGRAM, "vscode", [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::IsNullOrWhiteSpace($env:VSCODE_IPC_HOOK_CLI) -or
            -not [string]::IsNullOrWhiteSpace($env:VSCODE_GIT_IPC_HANDLE)
        return $(if ($isVsCodeRemote) { "wsl-vscode-remote" } else { "wsl" })
    }
    if ($IsLinux) { return "linux" }
    if ($IsMacOS) { return "macos" }
    return "unknown"
}
function Invoke-ToolVersionProbe {
    param([string] $CommandName)
    $command = Get-Command $CommandName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        return [pscustomobject]@{
            resolved_path = $null
            output = "The '$CommandName' application was not found on PATH."
            exit_code = 127
        }
    }
    $resolvedPath = if ($command.Path) { $command.Path } else { $command.Source }
    try {
        $toolOutput = & $resolvedPath --version 2>&1
        $exitCode = $LASTEXITCODE
        $output = @($toolOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    }
    catch {
        $exitCode = 126
        $output = $_.Exception.Message
    }
    return [pscustomobject]@{
        resolved_path = $resolvedPath
        output = $output
        exit_code = $exitCode
    }
}
function Write-InternalToolProbe {
    param([string] $OutputPath)
    $fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
    $parentDirectory = Split-Path -Parent $fullOutputPath
    if ([string]::IsNullOrWhiteSpace($parentDirectory)) {
        throw "The internal tool-probe path must have a parent directory."
    }
    New-Item -ItemType Directory -Force -Path $parentDirectory | Out-Null
    $probe = [ordered]@{
        schema_version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString("o")
        host_kind = Get-BenchmarkHostKind
        ripgrep = Invoke-ToolVersionProbe -CommandName "rg"
        roslynkit = Invoke-ToolVersionProbe -CommandName "roslynkit"
    }
    $probe | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $fullOutputPath -Encoding UTF8
}
function Get-ObjectPropertyValue {
    param([object] $InputObject, [string] $Name)
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Get-ToolProbeValidationIssues {
    param([object] $Probe)
    $issues = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Probe) {
        $issues.Add("tool probe was missing")
        return $issues.ToArray()
    }
    if ((Get-ObjectPropertyValue -InputObject $Probe -Name "schema_version") -ne 1) {
        $issues.Add("schema_version was not 1")
    }
    $hostKind = [string] (Get-ObjectPropertyValue -InputObject $Probe -Name "host_kind")
    if ($hostKind -notin @("windows", "wsl", "wsl-vscode-remote", "linux", "macos", "unknown")) {
        $issues.Add("host_kind was missing or unsupported")
    }
    elseif (-not [string]::Equals($hostKind, (Get-BenchmarkHostKind), [System.StringComparison]::Ordinal)) {
        $issues.Add("host_kind did not match the controller host")
    }
    $expectedTools = @(
        [pscustomobject]@{ Name = "ripgrep"; VersionPattern = '(?im)^(?:ripgrep|rg)\s+\d' },
        [pscustomobject]@{ Name = "roslynkit"; VersionPattern = '(?i)roslynkit version' }
    )
    foreach ($expectedTool in $expectedTools) {
        $tool = Get-ObjectPropertyValue -InputObject $Probe -Name $expectedTool.Name
        if ($null -eq $tool) {
            $issues.Add("$($expectedTool.Name) probe was missing")
            continue
        }
        $resolvedPath = [string] (Get-ObjectPropertyValue -InputObject $tool -Name "resolved_path")
        $output = [string] (Get-ObjectPropertyValue -InputObject $tool -Name "output")
        $exitCode = Get-ObjectPropertyValue -InputObject $tool -Name "exit_code"
        $parsedExitCode = 0
        $hasNumericExitCode = $null -ne $exitCode -and [int]::TryParse([string] $exitCode, [ref] $parsedExitCode)
        if ([string]::IsNullOrWhiteSpace($resolvedPath)) {
            $issues.Add("$($expectedTool.Name) resolved path was missing")
        }
        elseif (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
            $issues.Add("$($expectedTool.Name) resolved path was not a file")
        }
        if (-not $hasNumericExitCode -or $parsedExitCode -ne 0) {
            $issues.Add("$($expectedTool.Name) exit code was not zero")
        }
        if ([string]::IsNullOrWhiteSpace($output) -or $output -notmatch $expectedTool.VersionPattern) {
            $issues.Add("$($expectedTool.Name) version output was invalid")
        }
    }
    return $issues.ToArray()
}
function Read-ValidatedToolProbe {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The child tool-probe artifact was not written: '$Path'."
    }
    try {
        $probe = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    }
    catch {
        throw "The child tool-probe artifact was not valid JSON: '$Path'."
    }
    $issues = @(Get-ToolProbeValidationIssues -Probe $probe)
    if ($issues.Count -gt 0) {
        throw "The child tool-probe artifact was invalid: $($issues -join '; ')."
    }
    return $probe
}
function Test-SingleSuccessfulCommandEvent {
    param([object[]] $Events)
    $completedCommands = @($Events | Where-Object {
            $_.type -eq "item.completed" -and $_.item.type -eq "command_execution"
        })
    return $completedCommands.Count -eq 1 -and
        $completedCommands[0].item.status -eq "completed" -and
        $completedCommands[0].item.exit_code -eq 0
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
    param([object] $Case, [string] $Condition, [int] $Trial, [string] $RepoRoot, [object[]] $RepositoryManifest, [string] $RunRoot, [string] $ResolvedRoslynKitPath, [string] $IndexPath, [string[]] $DisabledFeatures)
    $runId = "{0}-{1}-trial{2}" -f $Case.id, $Condition, $Trial
    $answerPath = Join-Path $RunRoot "answers\$runId.md"
    $eventPath = Join-Path $RunRoot "events\$runId.jsonl"
    $stderrPath = Join-Path $RunRoot "stderr\$runId.txt"
    $commandsPath = Join-Path $RunRoot "commands\$runId.txt"
    $prompt = New-ConditionPrompt -Condition $Condition -Prompt $Case.prompt -IndexPath $IndexPath
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
    $eventLog = Read-EventLog -Path $eventPath
    $events = @($eventLog.events)
    $commands = Get-Commands -Events $events
    $commands | Set-Content -LiteralPath $commandsPath -Encoding UTF8
    $accounting = Get-TokenAccounting -Events $events
    $usage = $accounting.usage
    $repositoryChanges = Get-RepositoryContentChanges -RepoRoot $RepoRoot -Baseline $RepositoryManifest
    $issues = @(
        Get-ComplianceIssues -Condition $Condition -Commands $commands -Events $events -RepositoryChanges $repositoryChanges -ResolvedRoslynKitPath $ResolvedRoslynKitPath -RepoRoot $RepoRoot -AllowedIndexPath $IndexPath
        $eventLog.issues
        $accounting.issues
    )
    if (-not (Test-NonEmptyFile -Path $answerPath)) { $issues = @($issues + "no final answer was written") }
    $inputTokens = Get-ObjectPropertyValue -InputObject $usage -Name "input_tokens"
    $cachedInputTokens = Get-ObjectPropertyValue -InputObject $usage -Name "cached_input_tokens"
    $cacheWriteInputTokens = Get-ObjectPropertyValue -InputObject $usage -Name "cache_write_input_tokens"
    $uncachedInputTokens = Get-ObjectPropertyValue -InputObject $usage -Name "uncached_input_tokens"
    $regularUncachedInputTokens = Get-ObjectPropertyValue -InputObject $usage -Name "regular_uncached_input_tokens"
    $outputTokens = Get-ObjectPropertyValue -InputObject $usage -Name "output_tokens"
    $reasoningOutputTokens = Get-ObjectPropertyValue -InputObject $usage -Name "reasoning_output_tokens"
    $solShortCost = Get-Gpt56CostProjection -Usage $usage -Model "gpt-5.6-sol" -ContextClass short
    $terraShortCost = Get-Gpt56CostProjection -Usage $usage -Model "gpt-5.6-terra" -ContextClass short
    $lunaShortCost = Get-Gpt56CostProjection -Usage $usage -Model "gpt-5.6-luna" -ContextClass short
    $selectedShortCost = Get-Gpt56CostProjection -Usage $usage -Model $Model -ContextClass short
    $selectedLongCost = Get-Gpt56CostProjection -Usage $usage -Model $Model -ContextClass long
    $eventsSha256 = if (Test-Path -LiteralPath $eventPath -PathType Leaf) { (Get-FileHash -LiteralPath $eventPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    $turnCount = @($events | Where-Object { $_.type -eq "turn.started" }).Count
    $roslynKitInvocationCount = Get-RoslynKitInvocationCount -Commands $commands -ResolvedRoslynKitPath $ResolvedRoslynKitPath
    return [pscustomobject]@{
        timestamp_utc = [DateTime]::UtcNow.ToString("o"); run_id = $runId; case_id = $Case.id; condition = $Condition; trial = $Trial
        model = $Model; reasoning_effort = $ReasoningEffort; valid = ($exitCode -eq 0 -and $null -ne $inputTokens -and $issues.Count -eq 0)
        exit_code = $exitCode; duration_seconds = [Math]::Round(($completedAt - $startedAt).TotalSeconds, 3)
        input_tokens = $inputTokens; cached_input_tokens = $cachedInputTokens; cache_write_input_tokens = $cacheWriteInputTokens
        uncached_input_tokens = $uncachedInputTokens; regular_uncached_input_tokens = $regularUncachedInputTokens
        output_tokens = $outputTokens; reasoning_output_tokens = $reasoningOutputTokens
        cache_hit_rate_pct = if ($null -ne $inputTokens -and $inputTokens -gt 0 -and $null -ne $cachedInputTokens) { [Math]::Round(100.0 * $cachedInputTokens / $inputTokens, 4) } else { $null }
        model_turn_count = $turnCount; tool_call_count = $commands.Count; command_count = $commands.Count
        roslynkit_invocation_count = $roslynKitInvocationCount
        usage_source = $accounting.usage_source; usage_scope = $accounting.usage_scope
        request_usage_available = $accounting.request_usage_available
        max_request_input_tokens = $accounting.max_request_input_tokens
        requests_over_272k = $accounting.requests_over_long_context_threshold
        long_context_pricing_status = "unknown_request_level_usage_unavailable"
        selected_model_short_context_cost_usd = Get-ObjectPropertyValue -InputObject $selectedShortCost -Name "total_cost_usd"
        selected_model_all_long_context_cost_usd = Get-ObjectPropertyValue -InputObject $selectedLongCost -Name "total_cost_usd"
        cost_projection_status = Get-ObjectPropertyValue -InputObject $selectedShortCost -Name "status"
        sol_short_context_cost_usd = Get-ObjectPropertyValue -InputObject $solShortCost -Name "total_cost_usd"
        terra_short_context_cost_usd = Get-ObjectPropertyValue -InputObject $terraShortCost -Name "total_cost_usd"
        luna_short_context_cost_usd = Get-ObjectPropertyValue -InputObject $lunaShortCost -Name "total_cost_usd"
        sol_regular_uncached_input_cost_usd = Get-ObjectPropertyValue -InputObject $solShortCost -Name "regular_uncached_input_cost_usd"
        sol_cached_input_cost_usd = Get-ObjectPropertyValue -InputObject $solShortCost -Name "cached_input_cost_usd"
        sol_cache_write_cost_usd = Get-ObjectPropertyValue -InputObject $solShortCost -Name "cache_write_cost_usd"
        sol_output_cost_usd = Get-ObjectPropertyValue -InputObject $solShortCost -Name "output_cost_usd"
        pricing_source = $Gpt56PricingSource; pricing_verified_date = $Gpt56PricingVerifiedDate
        issues = ($issues -join " | "); answer_path = $answerPath
        events_path = $eventPath; events_sha256 = $eventsSha256; stderr_path = $stderrPath; commands_path = $commandsPath
    }
}
function Invoke-BenchmarkPreflight {
    param([string] $RepoRoot, [object[]] $RepositoryManifest, [string] $RunRoot, [string[]] $DisabledFeatures)
    $preflightRoot = Join-Path $RunRoot "preflight"
    New-Item -ItemType Directory -Force -Path $preflightRoot | Out-Null
    $answerPath = Join-Path $preflightRoot "answer.md"
    $eventPath = Join-Path $preflightRoot "events.jsonl"
    $stderrPath = Join-Path $preflightRoot "stderr.txt"
    $commandsPath = Join-Path $preflightRoot "commands.txt"
    $probePath = Join-Path $preflightRoot "tool-probe.json"
    $probeRelativePath = [IO.Path]::GetRelativePath($RepoRoot, $probePath).Replace("\", "/")
    if ($probeRelativePath -eq ".." -or $probeRelativePath.StartsWith("../", [System.StringComparison]::Ordinal)) {
        throw "The tool-probe artifact must be below the repository root."
    }
    if (-not $probeRelativePath.StartsWith("./", [System.StringComparison]::Ordinal)) {
        $probeRelativePath = "./$probeRelativePath"
    }
    $preflightCommand = "pwsh -NoProfile -File ./scripts/benchmark-codex.ps1 -InternalToolProbePath '$probeRelativePath'"
    $prompt = "Run exactly this one shell command once and do not run any other command:`n`n$preflightCommand`n`nThen reply with exactly: tool probe complete"
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
    $eventLog = Read-EventLog -Path $eventPath
    $events = @($eventLog.events)
    $commands = Get-Commands -Events $events
    $commands | Set-Content -LiteralPath $commandsPath -Encoding UTF8
    if ($exitCode -ne 0 -or $eventLog.issues.Count -gt 0 -or -not (Test-SingleSuccessfulCommandEvent -Events $events)) {
        throw "Benchmark preflight failed before measured sessions. Inspect '$preflightRoot'."
    }
    try {
        $probe = Read-ValidatedToolProbe -Path $probePath
    }
    catch {
        throw "Benchmark preflight failed before measured sessions. $($_.Exception.Message) Inspect '$preflightRoot'."
    }
    $repositoryChanges = Get-RepositoryContentChanges -RepoRoot $RepoRoot -Baseline $RepositoryManifest
    if ($repositoryChanges.Count -gt 0) {
        throw "Repository content changed during benchmark preflight: $($repositoryChanges -join '; ')"
    }
    Write-Host "Benchmark preflight passed: $preflightRoot"
    return $probe
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
function Format-Currency {
    param($Value)
    if ($null -eq $Value) { return "" }; return '$' + ([double] $Value).ToString("0.000000", [Globalization.CultureInfo]::InvariantCulture)
}
function Format-Percent {
    param($Value)
    if ($null -eq $Value) { return "" }; return ([double] $Value).ToString("0.##", [Globalization.CultureInfo]::InvariantCulture) + "%"
}
function Get-SavingsPercent {
    param($Raw, $RoslynKit)
    if ($null -eq $Raw -or $null -eq $RoslynKit -or $Raw -le 0) { return $null }; return 100.0 * ($Raw - $RoslynKit) / $Raw
}
function Sync-ReviewResults {
    param([string] $RunRoot, [object[]] $Rows, [object[]] $Cases)
    $path = Join-Path $RunRoot "review-results.json"
    $existingByRunId = @{}
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        try {
            $document = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        }
        catch {
            throw "Review results were not valid JSON: $path"
        }
        if ((Get-ObjectPropertyValue -InputObject $document -Name "schema_version") -ne 1) {
            throw "Review results must use schema_version 1: $path"
        }
        foreach ($entry in @((Get-ObjectPropertyValue -InputObject $document -Name "runs"))) {
            $entryRunId = [string] (Get-ObjectPropertyValue -InputObject $entry -Name "run_id")
            if (-not [string]::IsNullOrWhiteSpace($entryRunId)) { $existingByRunId[$entryRunId] = $entry }
        }
    }
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($row in $Rows) {
        $runId = [string] (Get-ObjectPropertyValue -InputObject $row -Name "run_id")
        if ([string]::IsNullOrWhiteSpace($runId)) {
            $runId = "{0}-{1}-trial{2}" -f $row.case_id, $row.condition, $row.trial
        }
        $case = @($Cases | Where-Object { $_.id -eq $row.case_id } | Select-Object -First 1)
        $criteria = New-Object System.Collections.Generic.List[object]
        $existing = if ($existingByRunId.ContainsKey($runId)) { $existingByRunId[$runId] } else { $null }
        $existingCriteriaById = @{}
        foreach ($criterion in @((Get-ObjectPropertyValue -InputObject $existing -Name "criteria"))) {
            $criterionId = [string] (Get-ObjectPropertyValue -InputObject $criterion -Name "id")
            if (-not [string]::IsNullOrWhiteSpace($criterionId)) { $existingCriteriaById[$criterionId] = $criterion }
        }
        for ($index = 0; $index -lt $case[0].manualReviewCriteria.Count; $index++) {
            $criterionId = "criterion-{0}" -f ($index + 1)
            $existingCriterion = if ($existingCriteriaById.ContainsKey($criterionId)) { $existingCriteriaById[$criterionId] } else { $null }
            $criterionStatus = [string] (Get-ObjectPropertyValue -InputObject $existingCriterion -Name "status")
            if ([string]::IsNullOrWhiteSpace($criterionStatus)) { $criterionStatus = "not_evaluated" }
            if ($criterionStatus -notin @("pass", "fail", "not_evaluated")) {
                throw "Review criterion '$runId/$criterionId' had unsupported status '$criterionStatus'."
            }
            $criteria.Add([pscustomobject]@{
                    id = $criterionId
                    text = [string] $case[0].manualReviewCriteria[$index]
                    status = $criterionStatus
                    evidence = [string] (Get-ObjectPropertyValue -InputObject $existingCriterion -Name "evidence")
                })
        }
        $overallStatus = [string] (Get-ObjectPropertyValue -InputObject $existing -Name "overall_status")
        if ([string]::IsNullOrWhiteSpace($overallStatus)) { $overallStatus = "not_evaluated" }
        if ($overallStatus -notin @("pass", "fail", "not_evaluated")) {
            throw "Review '$runId' had unsupported overall_status '$overallStatus'."
        }
        if ($overallStatus -eq "pass" -and @($criteria | Where-Object { $_.status -ne "pass" }).Count -gt 0) {
            throw "Review '$runId' cannot pass until every criterion passes."
        }
        $entries.Add([pscustomobject]@{
                run_id = $runId; case_id = $row.case_id; condition = $row.condition; trial = $row.trial
                overall_status = $overallStatus
                reviewer = [string] (Get-ObjectPropertyValue -InputObject $existing -Name "reviewer")
                reviewed_at_utc = Get-ObjectPropertyValue -InputObject $existing -Name "reviewed_at_utc"
                notes = [string] (Get-ObjectPropertyValue -InputObject $existing -Name "notes")
                criteria = $criteria.ToArray()
            })
    }
    $reviewDocument = [ordered]@{
        schema_version = 1
        instructions = "Set every criterion and overall_status to pass or fail. Cost-per-correct-answer comparisons use only operationally valid runs with overall_status=pass."
        runs = $entries.ToArray()
    }
    $reviewDocument | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding UTF8
    return $entries.ToArray()
}
function Write-Reports {
    param([string] $RunRoot, [object[]] $Rows, [object[]] $Cases)
    $Rows | Export-Csv -LiteralPath (Join-Path $RunRoot "runs.csv") -NoTypeInformation -Encoding UTF8
    $Rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RunRoot "runs.json") -Encoding UTF8
    $reviewResults = @(Sync-ReviewResults -RunRoot $RunRoot -Rows $Rows -Cases $Cases)
    $reviewByRunId = @{}
    foreach ($result in $reviewResults) { $reviewByRunId[[string] $result.run_id] = $result }
    $summary = @(
        "# Codex Benchmark", "",
        "GPT-5.6 cost values are Standard API short-context projections using prices verified $Gpt56PricingVerifiedDate from [$Gpt56PricingSource]($Gpt56PricingSource). They are not a claim about the active Codex account's bill.", "",
        "``codex exec --json`` exposes one cumulative completed-turn total, not usage for each underlying model request. Request-level 272K threshold metrics and exact long-context cost are therefore unavailable in this runner version.", "",
        "## By Case And Condition", "",
        "| Case | Condition | Valid | Correct | Pending review | Median input | Cached | Cache write | Regular uncached | Output | Reasoning output | Cache rate | Turns | Tool calls | RoslynKit calls | Duration (s) |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"
    )
    $valid = @($Rows | Where-Object { $_.valid -eq $true -and $null -ne $_.input_tokens })
    foreach ($group in $Rows | Group-Object case_id, condition | Sort-Object Name) {
        $validRows = @($group.Group | Where-Object { $_.valid -and $null -ne $_.input_tokens })
        $correctRows = @($validRows | Where-Object { $reviewByRunId[[string] $_.run_id].overall_status -eq "pass" })
        $pendingRows = @($validRows | Where-Object { $reviewByRunId[[string] $_.run_id].overall_status -eq "not_evaluated" })
        $input = Get-Median -Values @($validRows | ForEach-Object { $_.input_tokens })
        $cached = Get-Median -Values @($validRows | Where-Object { $null -ne $_.cached_input_tokens } | ForEach-Object { $_.cached_input_tokens })
        $cacheWrite = Get-Median -Values @($validRows | Where-Object { $null -ne $_.cache_write_input_tokens } | ForEach-Object { $_.cache_write_input_tokens })
        $regularUncached = Get-Median -Values @($validRows | Where-Object { $null -ne $_.regular_uncached_input_tokens } | ForEach-Object { $_.regular_uncached_input_tokens })
        $output = Get-Median -Values @($validRows | Where-Object { $null -ne $_.output_tokens } | ForEach-Object { $_.output_tokens })
        $reasoningOutput = Get-Median -Values @($validRows | Where-Object { $null -ne $_.reasoning_output_tokens } | ForEach-Object { $_.reasoning_output_tokens })
        $cacheRate = Get-Median -Values @($validRows | Where-Object { $null -ne $_.cache_hit_rate_pct } | ForEach-Object { $_.cache_hit_rate_pct })
        $turns = Get-Median -Values @($validRows | ForEach-Object { $_.model_turn_count })
        $toolCalls = Get-Median -Values @($validRows | ForEach-Object { $_.tool_call_count })
        $roslynKitCalls = Get-Median -Values @($validRows | ForEach-Object {
                $recorded = Get-ObjectPropertyValue -InputObject $_ -Name "roslynkit_invocation_count"
                if ($null -ne $recorded) { $recorded }
                elseif ($_.condition -eq "raw-codex") { 0 }
                elseif (-not [string]::IsNullOrWhiteSpace([string] $_.commands_path) -and (Test-Path -LiteralPath $_.commands_path -PathType Leaf)) {
                    Get-RoslynKitInvocationCount -Commands @(Get-Content -LiteralPath $_.commands_path) -ResolvedRoslynKitPath "roslynkit"
                }
            })
        $duration = Get-Median -Values @($validRows | ForEach-Object { $_.duration_seconds })
        $parts = $group.Name -split ", "
        $summary += "| $($parts[0]) | $($parts[1]) | $($validRows.Count) | $($correctRows.Count) | $($pendingRows.Count) | $(Format-Metric $input) | $(Format-Metric $cached) | $(Format-Metric $cacheWrite) | $(Format-Metric $regularUncached) | $(Format-Metric $output) | $(Format-Metric $reasoningOutput) | $(Format-Percent $cacheRate) | $(Format-Metric $turns) | $(Format-Metric $toolCalls) | $(Format-Metric $roslynKitCalls) | $(Format-Metric $duration) |"
    }
    $reviewedCorrect = @($valid | Where-Object { $reviewByRunId[[string] $_.run_id].overall_status -eq "pass" })
    $summary += @(
        "", "## Cost Per Correct Answer", "",
        "Only operationally valid runs marked ``pass`` in ``review-results.json`` appear here.", "",
        "| Case | Model | Raw correct | RoslynKit correct | Raw median projected cost | RoslynKit median projected cost | Cost savings | Raw short/all-long range | RoslynKit short/all-long range |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | --- |"
    )
    foreach ($case in $Cases) {
        $caseRows = @($reviewedCorrect | Where-Object { $_.case_id -eq $case.id })
        foreach ($modelName in @($Rows | Where-Object { $_.case_id -eq $case.id } | ForEach-Object { $_.model } | Select-Object -Unique)) {
            $raw = @($caseRows | Where-Object { $_.condition -eq "raw-codex" -and $_.model -eq $modelName })
            $roslynKit = @($caseRows | Where-Object { $_.condition -eq "roslynkit" -and $_.model -eq $modelName })
            $rawCost = Get-Median -Values @($raw | Where-Object { $null -ne $_.selected_model_short_context_cost_usd } | ForEach-Object { $_.selected_model_short_context_cost_usd })
            $roslynCost = Get-Median -Values @($roslynKit | Where-Object { $null -ne $_.selected_model_short_context_cost_usd } | ForEach-Object { $_.selected_model_short_context_cost_usd })
            $rawLong = Get-Median -Values @($raw | Where-Object { $null -ne $_.selected_model_all_long_context_cost_usd } | ForEach-Object { $_.selected_model_all_long_context_cost_usd })
            $roslynLong = Get-Median -Values @($roslynKit | Where-Object { $null -ne $_.selected_model_all_long_context_cost_usd } | ForEach-Object { $_.selected_model_all_long_context_cost_usd })
            $costSavings = Get-SavingsPercent $rawCost $roslynCost
            $summary += "| $($case.id) | $modelName | $($raw.Count) | $($roslynKit.Count) | $(Format-Currency $rawCost) | $(Format-Currency $roslynCost) | $(Format-Percent $costSavings) | $(Format-Currency $rawCost)–$(Format-Currency $rawLong) | $(Format-Currency $roslynCost)–$(Format-Currency $roslynLong) |"
        }
    }
    $summary += @(
        "", "## Token Savings For Correct Answers", "",
        "| Case | Model | Input | Cached input | Cache writes | Regular uncached input | Output |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: |"
    )
    foreach ($case in $Cases) {
        foreach ($modelName in @($Rows | Where-Object { $_.case_id -eq $case.id } | ForEach-Object { $_.model } | Select-Object -Unique)) {
            $raw = @($reviewedCorrect | Where-Object { $_.case_id -eq $case.id -and $_.condition -eq "raw-codex" -and $_.model -eq $modelName })
            $roslynKit = @($reviewedCorrect | Where-Object { $_.case_id -eq $case.id -and $_.condition -eq "roslynkit" -and $_.model -eq $modelName })
            $metricNames = @("input_tokens", "cached_input_tokens", "cache_write_input_tokens", "regular_uncached_input_tokens", "output_tokens")
            $savings = New-Object System.Collections.Generic.List[string]
            foreach ($metricName in $metricNames) {
                $rawMedian = Get-Median -Values @($raw | ForEach-Object { Get-ObjectPropertyValue -InputObject $_ -Name $metricName } | Where-Object { $null -ne $_ })
                $roslynMedian = Get-Median -Values @($roslynKit | ForEach-Object { Get-ObjectPropertyValue -InputObject $_ -Name $metricName } | Where-Object { $null -ne $_ })
                $savings.Add((Format-Percent (Get-SavingsPercent $rawMedian $roslynMedian)))
            }
            $summary += "| $($case.id) | $modelName | $($savings[0]) | $($savings[1]) | $($savings[2]) | $($savings[3]) | $($savings[4]) |"
        }
    }
    $summary += @(
        "", "## GPT-5.6 Standard Cost Projections For Correct Runs", "",
        "These projections apply each model's price to the measured token profile; they do not predict how another model would navigate the task.", "",
        "| Case | Condition | Executed model | Sol | Terra | Luna |",
        "| --- | --- | --- | ---: | ---: | ---: |"
    )
    foreach ($group in $reviewedCorrect | Group-Object case_id, condition, model | Sort-Object Name) {
        $parts = $group.Name -split ", "
        $solCost = Get-Median -Values @($group.Group | ForEach-Object { $_.sol_short_context_cost_usd })
        $terraCost = Get-Median -Values @($group.Group | ForEach-Object { $_.terra_short_context_cost_usd })
        $lunaCost = Get-Median -Values @($group.Group | ForEach-Object { $_.luna_short_context_cost_usd })
        $summary += "| $($parts[0]) | $($parts[1]) | $($parts[2]) | $(Format-Currency $solCost) | $(Format-Currency $terraCost) | $(Format-Currency $lunaCost) |"
    }
    if ($reviewedCorrect.Count -eq 0) { $summary += "| — | — | — | — | — | — |" }
    $summary += @(
        "", "## Sol Standard Cost Breakdown For Correct Runs", "",
        "| Case | Condition | Executed model | Regular uncached input | Cached input | Cache writes | Output | Total |",
        "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |"
    )
    foreach ($group in $reviewedCorrect | Group-Object case_id, condition, model | Sort-Object Name) {
        $parts = $group.Name -split ", "
        $regularCost = Get-Median -Values @($group.Group | ForEach-Object { $_.sol_regular_uncached_input_cost_usd })
        $cachedCost = Get-Median -Values @($group.Group | ForEach-Object { $_.sol_cached_input_cost_usd })
        $cacheWriteCost = Get-Median -Values @($group.Group | ForEach-Object { $_.sol_cache_write_cost_usd })
        $outputCost = Get-Median -Values @($group.Group | ForEach-Object { $_.sol_output_cost_usd })
        $totalCost = Get-Median -Values @($group.Group | ForEach-Object { $_.sol_short_context_cost_usd })
        $summary += "| $($parts[0]) | $($parts[1]) | $($parts[2]) | $(Format-Currency $regularCost) | $(Format-Currency $cachedCost) | $(Format-Currency $cacheWriteCost) | $(Format-Currency $outputCost) | $(Format-Currency $totalCost) |"
    }
    if ($reviewedCorrect.Count -eq 0) { $summary += "| — | — | — | — | — | — | — | — |" }
    $invalid = @($Rows | Where-Object { $_.valid -ne $true })
    $summary += @("", "## Invalid Runs", "")
    if ($invalid.Count -eq 0) { $summary += "None." }
    else {
        $summary += "| Case | Condition | Trial | Exit | Issues |", "| --- | --- | ---: | ---: | --- |"
        foreach ($row in $invalid) { $summary += "| $($row.case_id) | $($row.condition) | $($row.trial) | $($row.exit_code) | $($row.issues -replace '\|', '/') |" }
    }
    $summary | Set-Content -LiteralPath (Join-Path $RunRoot "summary.md") -Encoding UTF8
    $review = @("# Manual Review", "", "Record criterion results and the overall result in ``review-results.json``, then rerun the report-only command shown below. These criteria are never included in child prompts.", "", '```powershell', "pwsh -NoProfile -File ./scripts/benchmark-codex.ps1 -ReportRunRoot '$RunRoot'", '```')
    foreach ($case in $Cases) {
        $review += ""
        $review += "## $($case.id)"
        $review += ""
        foreach ($criterion in $case.manualReviewCriteria) { $review += "- $criterion" }
        foreach ($row in $Rows | Where-Object { $_.case_id -eq $case.id }) {
            $status = $reviewByRunId[[string] $row.run_id].overall_status
            $review += "- $($row.condition) trial $($row.trial): $($row.answer_path) (valid: $($row.valid); review: $status)"
        }
    }
    $review | Set-Content -LiteralPath (Join-Path $RunRoot "review.md") -Encoding UTF8
}
if ($MyInvocation.InvocationName -eq ".") {
    return
}
if (-not [string]::IsNullOrWhiteSpace($InternalToolProbePath)) {
    Write-InternalToolProbe -OutputPath $InternalToolProbePath
    return
}
[Environment]::SetEnvironmentVariable("CODEX_THREAD_ID", $null, "Process")
$repoRoot = Resolve-RepoRoot
$resolvedRoslynKitPath = $null
$allCases = Get-CaseData -RepoRoot $repoRoot
if (-not [string]::IsNullOrWhiteSpace($ReportRunRoot)) {
    if ($DryRun) { throw "ReportRunRoot cannot be combined with DryRun." }
    $resolvedReportRunRoot = Resolve-BenchmarkReportRunRoot -RepoRoot $repoRoot -Path $ReportRunRoot
    $reportRows = @((Get-Content -Raw -LiteralPath (Join-Path $resolvedReportRunRoot "runs.json") | ConvertFrom-Json))
    $reportCaseIds = @($reportRows | ForEach-Object { $_.case_id } | Select-Object -Unique)
    $reportCases = @($allCases | Where-Object { $reportCaseIds -contains $_.id })
    Write-Reports -RunRoot $resolvedReportRunRoot -Rows $reportRows -Cases $reportCases
    Write-Host "Benchmark reports refreshed: $resolvedReportRunRoot"
    return
}
$benchmarkIndexPath = Resolve-BenchmarkIndexPath -RepoRoot $repoRoot -Path $IndexPath
$cases = Get-SelectedCases -Cases $allCases -SelectedCaseId $CaseId
$activeCodexConfigPath = Set-HostCodexHome
if ($DryRun) {
    $placeholderRepoRoot = "<repository-root>"
    $disabledFeatures = Get-DisabledFeatures -DryRunMode
    Write-Host "Active Codex config: $activeCodexConfigPath"
    Write-Host "Environment: the current host's CODEX_HOME is used directly; benchmark-specific command-line overrides remain in effect."
    Write-Host "Execution: child sessions bypass approvals and sandboxing, inherit the full host environment, disable unified_exec, and use the repository root as the --cd working root."
    Write-Host "RoslynKit condition: the global 'roslynkit' command is resolved from the inherited host PATH; the prepared search index is $benchmarkIndexPath relative to the repository root."
    Write-Host "Preflight: one unmeasured child runs the controller's hidden tool-probe mode through pwsh and writes structured host, path, output, and exit-code evidence."
    Write-Host "Comparison: compare raw Codex with RoslynKit only inside the same run and host; do not compare duration across hosts or with runs made before unified_exec was disabled."
    Write-Host "Validity: an invalid measured session is recorded and excluded from comparison, then the remaining scheduled sessions continue without retry. Preparation, preflight, and nonignored repository content changes stop the controller."
    Write-Host "Cost: reports project GPT-5.6 Sol, Terra, and Luna Standard API prices verified $Gpt56PricingVerifiedDate; correctness-gated savings remain empty until review-results.json is completed."
    Write-Host "Long context: Codex exec JSONL exposes completed-turn aggregate usage, so request-level 272K threshold counts and exact long-context cost remain unknown."
    Write-Host "Repository integrity: a content manifest is captured before preflight and validated after preflight, preparation, and every measured session; ignored artifacts do not affect it."
    Write-Host ""
    foreach ($trial in 1..$Trials) {
        $conditions = if (($trial % 2) -eq 1) { @("raw-codex", "roslynkit") } else { @("roslynkit", "raw-codex") }
        foreach ($case in $cases) {
            foreach ($condition in $conditions) {
                $prompt = New-ConditionPrompt -Condition $condition -Prompt $case.prompt -IndexPath $benchmarkIndexPath
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
if ($null -eq (Get-Command codex -ErrorAction SilentlyContinue)) {
    throw "The installed 'codex' executable is required."
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
    $repositoryManifest = Get-RepositoryContentManifest -RepoRoot $repoRoot
    $toolProbe = Invoke-BenchmarkPreflight -RepoRoot $repoRoot -RepositoryManifest $repositoryManifest -RunRoot $runRoot -DisabledFeatures $disabledFeatures
    $resolvedRoslynKitPath = [string] $toolProbe.roslynkit.resolved_path
    Write-Host "Benchmark host: $($toolProbe.host_kind). Timing comparisons are valid only within this run."
    Initialize-RoslynKitIndex -RepoRoot $repoRoot -ResolvedRoslynKitPath $resolvedRoslynKitPath -IndexPath $benchmarkIndexPath
    Stop-RoslynKitDaemon -RepoRoot $repoRoot -ResolvedRoslynKitPath $resolvedRoslynKitPath
    $preparationChanges = Get-RepositoryContentChanges -RepoRoot $repoRoot -Baseline $repositoryManifest
    if ($preparationChanges.Count -gt 0) {
        throw "Repository content changed during benchmark preparation: $($preparationChanges -join '; ')"
    }
    foreach ($trial in 1..$Trials) {
        $conditions = if (($trial % 2) -eq 1) { @("raw-codex", "roslynkit") } else { @("roslynkit", "raw-codex") }
        foreach ($case in $cases) {
            foreach ($condition in $conditions) {
                try {
                    $row = Invoke-BenchmarkRun -Case $case -Condition $condition -Trial $trial -RepoRoot $repoRoot -RepositoryManifest $repositoryManifest -RunRoot $runRoot -ResolvedRoslynKitPath $resolvedRoslynKitPath -IndexPath $benchmarkIndexPath -DisabledFeatures $disabledFeatures
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
