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

$commitContextSkill = Get-Content -Raw -LiteralPath (Join-Path $repoRoot ".agents/skills/commit-context/SKILL.md")
Assert-True -Condition $commitContextSkill.Contains('one `Co-authored-by` trailer per contributing coding agent') -Message "The commit-context skill did not select trailers from contributing coding agents."
Assert-True -Condition $commitContextSkill.Contains('`Codex` -> `codex <242516109+codex@users.noreply.github.com>`') -Message "The commit-context skill did not map the Codex runtime identity."
Assert-True -Condition $commitContextSkill.Contains('`GitHub Copilot` or `Copilot` -> `Copilot <223556219+Copilot@users.noreply.github.com>`') -Message "The commit-context skill did not map the Copilot runtime identity."
Assert-True -Condition $commitContextSkill.Contains('`Cursor` -> `Cursor <cursoragent@cursor.com>`') -Message "The commit-context skill did not map the Cursor runtime identity."
Assert-True -Condition $commitContextSkill.Contains('automatically without requiring an invocation modifier') -Message "The commit-context skill did not automatically attribute a materially contributing active agent."

$securityAuditSkill = Get-Content -Raw -LiteralPath (Join-Path $repoRoot ".agents/skills/security-audit/SKILL.md")
$securityAuditScannerProbe = "pwsh -NoProfile -Command 'Get-Command gitleaks,trufflehog -ErrorAction SilentlyContinue'"
Assert-True -Condition $securityAuditSkill.Contains($securityAuditScannerProbe) -Message "The security-audit scanner probe did not use an explicit PowerShell host."
Assert-True -Condition $securityAuditSkill.Contains("do not submit individual PowerShell cmdlets to Bash") -Message "The security-audit multiline commands did not require an explicit PowerShell execution context."

$roslynKitDevSkill = Get-Content -Raw -LiteralPath (Join-Path $repoRoot ".agents/skills/roslynkit-dev/SKILL.md")
Assert-True -Condition $roslynKitDevSkill.Contains("--max-results 25") -Message "The development skill did not begin search discovery with 25 results."
Assert-True -Condition $roslynKitDevSkill.Contains('increasing to `--max-results 50`') -Message "The development skill did not increase refined search discovery to 50 results."
Assert-True -Condition $roslynKitDevSkill.Contains('one third and final search with `--max-results 200`') -Message "The development skill did not cap final fallback expansion at 200 results."

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

& {
    $packagingTestRoot = Join-Path $repoRoot ("artifacts/packaging-regression/" + [Guid]::NewGuid())
    $environmentNames = @(
        "NUGET_PACKAGES",
        "DOTNET_CLI_HOME",
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DOTNET_NOLOGO",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"
    )
    $originalEnvironment = @{}
    foreach ($name in $environmentNames) {
        $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }

    try {
        New-Item -ItemType Directory -Path $packagingTestRoot | Out-Null
        $nativeContext = [pscustomobject]@{ DotNet = (Get-Command pwsh -ErrorAction Stop).Source }
        $quietOutput = @(& {
            $VerbosePreference = "SilentlyContinue"
            Invoke-DotNet -Context $nativeContext -Arguments @(
                "-NoProfile", "-Command", '[Console]::Out.WriteLine("native-success"); [Console]::Error.WriteLine("native-warning"); exit 0'
            )
        } *>&1)
        Assert-True -Condition ($quietOutput.Count -eq 0) -Message "Successful Invoke-DotNet output was not quiet by default."

        $verboseOutput = @(& {
            $VerbosePreference = "Continue"
            Invoke-DotNet -Context $nativeContext -Arguments @(
                "-NoProfile", "-Command", '[Console]::Out.WriteLine("native-success"); [Console]::Error.WriteLine("native-warning"); exit 0'
            )
        } 4>&1)
        $verboseText = ($verboseOutput | ForEach-Object { $_.ToString() }) -join "`n"
        Assert-True -Condition $verboseText.Contains("native-success") -Message "Verbose Invoke-DotNet output omitted stdout."
        Assert-True -Condition $verboseText.Contains("native-warning") -Message "Verbose Invoke-DotNet output omitted stderr."
        Assert-True -Condition (@($verboseOutput | Where-Object { $_ -isnot [System.Management.Automation.VerboseRecord] }).Count -eq 0) -Message "Invoke-DotNet contaminated the success stream while verbose."

        $nativeFailure = @{ Error = $null }
        Assert-Throws -Action {
            try {
                Invoke-DotNet -Context $nativeContext -Arguments @(
                    "-NoProfile", "-Command", '[Console]::Out.WriteLine("failure-stdout"); [Console]::Error.WriteLine("failure-stderr"); exit 23'
                )
            }
            catch {
                $nativeFailure.Error = $_
                throw
            }
        } -Message "Invoke-DotNet accepted a failing native command."
        $failureMessage = $nativeFailure.Error.Exception.Message
        foreach ($expectedText in @("exit code 23", "failure-stdout", "failure-stderr")) {
            Assert-True -Condition $failureMessage.Contains($expectedText) -Message "Invoke-DotNet failure omitted '$expectedText'."
        }

        foreach ($testScriptName in @("test-roslynkit-global.ps1", "test-roslynkit-commands.ps1")) {
            $tokens = $null
            $parseErrors = $null
            $scriptAst = [System.Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $repoRoot "scripts/$testScriptName"), [ref]$tokens, [ref]$parseErrors)
            Assert-True -Condition ($parseErrors.Count -eq 0) -Message "$testScriptName did not parse."
            # Bind only the real parameter block; never execute a release script body.
            $parameterAttributes = ($scriptAst.ParamBlock.Attributes | ForEach-Object { $_.Extent.Text }) -join "`n"
            $bindingProbe = [scriptblock]::Create($parameterAttributes + "`n" + $scriptAst.ParamBlock.Extent.Text + "`nthrow 'Parameter probe body was reached.'")
            $probeArguments = @{ PrintManualCommands = $true }
            if ($testScriptName -eq "test-roslynkit-commands.ps1") {
                $probeArguments.CommandPath = "unused-command"
            }
            $bindingFailure = @{ Error = $null }
            Assert-Throws -Action {
                try {
                    & $bindingProbe @probeArguments
                }
                catch {
                    $bindingFailure.Error = $_
                    throw
                }
            } -Message "$testScriptName accepted -PrintManualCommands."
            Assert-True -Condition ($bindingFailure.Error.Exception -is [System.Management.Automation.ParameterBindingException]) -Message "$testScriptName did not reject -PrintManualCommands during parameter binding."
            Assert-True -Condition ($bindingFailure.Error.FullyQualifiedErrorId -like "NamedParameterNotFound*") -Message "$testScriptName rejected the probe for an unexpected reason."
            Assert-True -Condition $bindingFailure.Error.Exception.Message.Contains("PrintManualCommands") -Message "$testScriptName did not identify the removed parameter."
        }

        $context = [pscustomobject]@{
            RepoRoot = Join-Path $packagingTestRoot "repository"
            PackageFeedPath = Join-Path $packagingTestRoot "local feed & packages"
            PackageId = "roslynkit"
            PackageVersion = "1.2.3-test.4"
        }
        $expectedContext = $context
        New-Item -ItemType Directory -Path $context.RepoRoot, $context.PackageFeedPath | Out-Null
        $packagePath = Get-RoslynKitPackagePath -Context $context
        $validationRoot = Join-Path $context.RepoRoot "validation"
        $toolPath = Join-Path $validationRoot "tool"
        $expectedCommandPath = Get-RoslynKitToolCommandPath -ToolPath $toolPath
        $configPath = Join-Path $validationRoot "NuGet.Config"
        $sentinelPath = Join-Path $context.RepoRoot "preserved.txt"
        Set-Content -LiteralPath $sentinelPath -Value "preserved"
        $expectedEnvironment = @{
            NUGET_PACKAGES = Join-Path $validationRoot "nuget-packages"
            DOTNET_CLI_HOME = Join-Path $validationRoot "dotnet-cli-home"
            DOTNET_CLI_TELEMETRY_OPTOUT = "1"
            DOTNET_NOLOGO = "1"
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
        }
        $previousEnvironment = @{
            NUGET_PACKAGES = Join-Path $packagingTestRoot "original-nuget-packages"
            DOTNET_CLI_HOME = Join-Path $packagingTestRoot "original-cli-home"
            DOTNET_CLI_TELEMETRY_OPTOUT = "0"
            DOTNET_NOLOGO = "original-nologo"
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "original-first-time-experience"
        }
        foreach ($name in $environmentNames) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
        }

        function Invoke-DotNet {
            param([pscustomobject] $Context, [string[]] $Arguments)
            $state.InstallCalls++
            Assert-True -Condition ([object]::ReferenceEquals($Context, $expectedContext)) -Message "Package validation changed the install context."
            $expectedArguments = @(
                "tool", "install", $context.PackageId,
                "--tool-path", $toolPath,
                "--configfile", $configPath,
                "--version", $context.PackageVersion,
                "--ignore-failed-sources"
            )
            Assert-True -Condition (($Arguments -join "`n") -ceq ($expectedArguments -join "`n")) -Message "Package validation did not install the exact local version into the staging tool path."
            foreach ($name in $environmentNames) {
                Assert-True -Condition ([Environment]::GetEnvironmentVariable($name) -ceq $expectedEnvironment[$name]) -Message "Install did not isolate $name."
            }
            [xml]$config = Get-Content -Raw -LiteralPath $configPath
            Assert-True -Condition ($config.SelectNodes("/configuration/packageSources/*").Count -eq 2) -Message "The local NuGet config contained unexpected sources."
            Assert-True -Condition ($config.configuration.packageSources.FirstChild.Name -eq "clear") -Message "The local NuGet config did not clear inherited sources first."
            Assert-True -Condition ($config.configuration.packageSources.add.key -ceq "roslynkit-local") -Message "The local NuGet source key was unexpected."
            Assert-True -Condition ($config.configuration.packageSources.add.value -ceq $context.PackageFeedPath) -Message "The local NuGet source path did not survive XML escaping."
            if ($state.Scenario -eq "install-failure") {
                throw "Mock installation failed."
            }
        }

        function Assert-RoslynKitCommandVersion {
            param([string] $CommandPath, [string] $ExpectedVersion)
            $state.VersionCalls++
            Assert-True -Condition ($state.InstallCalls -eq 1) -Message "Version validation ran before installation."
            Assert-True -Condition ($CommandPath -ceq $expectedCommandPath) -Message "Version validation used the wrong command path."
            Assert-True -Condition ($ExpectedVersion -ceq $context.PackageVersion) -Message "Version validation used the wrong version."
            if ($state.Scenario -eq "version-rejection") {
                throw "Mock version rejected."
            }
        }

        foreach ($scenario in @("success", "install-failure", "version-rejection", "callback-failure", "package-mutation")) {
            $state = @{ Scenario = $scenario; InstallCalls = 0; VersionCalls = 0; CallbackCalls = 0; Error = $null }
            Set-Content -LiteralPath $packagePath -Value "original package"
            $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
            New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
            $stalePath = Join-Path $validationRoot "stale.txt"
            Set-Content -LiteralPath $stalePath -Value "stale"
            $validationAction = {
                param($installation)
                $state.CallbackCalls++
                Assert-True -Condition ($state.VersionCalls -eq 1) -Message "The callback ran before version validation."
                Assert-True -Condition ($installation.CommandPath -ceq $expectedCommandPath) -Message "The callback received the wrong command path."
                Assert-True -Condition ($installation.NuGetConfigPath -ceq $configPath) -Message "The callback received the wrong NuGet config path."
                Assert-True -Condition ($installation.OriginalDotNetCliHome -ceq $previousEnvironment.DOTNET_CLI_HOME) -Message "The callback lost the original DOTNET_CLI_HOME."
                foreach ($name in $environmentNames) {
                    Assert-True -Condition ([Environment]::GetEnvironmentVariable($name) -ceq $expectedEnvironment[$name]) -Message "The callback did not retain isolated $name."
                }
                foreach ($directory in @($env:NUGET_PACKAGES, $env:DOTNET_CLI_HOME)) {
                    Assert-True -Condition (Test-Path -LiteralPath $directory -PathType Container) -Message "The isolated cache or CLI home was not created."
                }
                if ($state.Scenario -eq "callback-failure") {
                    throw "Mock callback failed."
                }
                if ($state.Scenario -eq "package-mutation") {
                    Set-Content -LiteralPath $packagePath -Value "changed package"
                }
                "callback output must not escape"
                [pscustomobject]@{ CallbackOutput = "must not escape" }
            }

            if ($scenario -eq "success") {
                $result = @(Invoke-RoslynKitPackageValidation -Context $context -ValidationRoot $validationRoot -Action $validationAction)
                Assert-True -Condition ($result.Count -eq 1) -Message "Package validation contaminated its success output."
                Assert-True -Condition ($result[0].PackagePath -ceq $packagePath) -Message "Package validation returned the wrong package path."
                Assert-True -Condition ($result[0].PackageHash -ceq $packageHash) -Message "Package validation returned the wrong original hash."
                Assert-True -Condition ($result[0].CommandPath -ceq $expectedCommandPath) -Message "Package validation returned the wrong command path."
            }
            else {
                Assert-Throws -Action {
                    try {
                        Invoke-RoslynKitPackageValidation -Context $context -ValidationRoot $validationRoot -Action $validationAction
                    }
                    catch {
                        $state.Error = $_
                        throw
                    }
                } -Message "Package validation accepted $scenario."
                $expectedFailure = switch ($scenario) {
                    "install-failure" { "Mock installation failed." }
                    "version-rejection" { "Mock version rejected." }
                    "callback-failure" { "Mock callback failed." }
                    "package-mutation" { "The package changed during validation:" }
                }
                Assert-True -Condition $state.Error.Exception.Message.Contains($expectedFailure) -Message "$scenario failed for an unexpected reason: $($state.Error)"
            }

            Assert-True -Condition ($state.InstallCalls -eq 1) -Message "$scenario did not invoke installation exactly once."
            $expectedVersionCalls = if ($scenario -eq "install-failure") { 0 } else { 1 }
            $expectedCallbackCalls = if ($scenario -in @("install-failure", "version-rejection")) { 0 } else { 1 }
            Assert-True -Condition ($state.VersionCalls -eq $expectedVersionCalls) -Message "$scenario invoked version validation unexpectedly."
            Assert-True -Condition ($state.CallbackCalls -eq $expectedCallbackCalls) -Message "$scenario invoked the callback unexpectedly."
            Assert-False -Condition (Test-Path -LiteralPath $stalePath) -Message "$scenario did not reset the staging root."
            Assert-True -Condition (Test-Path -LiteralPath $sentinelPath -PathType Leaf) -Message "$scenario removed a file outside the staging root."
            foreach ($name in $environmentNames) {
                Assert-True -Condition ([Environment]::GetEnvironmentVariable($name) -ceq $previousEnvironment[$name]) -Message "$scenario did not restore $name."
            }
        }

        Assert-Throws -Action {
            Invoke-RoslynKitPackageValidation -Context $context -ValidationRoot $context.RepoRoot -Action { throw "The protected-root callback must not run." }
        } -Message "Package validation accepted its protected repository root."
        Assert-True -Condition (Test-Path -LiteralPath $sentinelPath -PathType Leaf) -Message "Package validation deleted its protected repository root."

        foreach ($name in $environmentNames) {
            if (Test-Path -LiteralPath "Env:$name") {
                Remove-Item -LiteralPath "Env:$name"
            }
        }
        $state = @{ Scenario = "success"; InstallCalls = 0; VersionCalls = 0; CallbackCalls = 0 }
        $null = Invoke-RoslynKitPackageValidation -Context $context -ValidationRoot $validationRoot -Action {
            param($installation)
            Assert-True -Condition ($null -eq $installation.OriginalDotNetCliHome) -Message "An absent original DOTNET_CLI_HOME was not preserved for the callback."
        }
        $notRestored = @($environmentNames | Where-Object { $null -ne [Environment]::GetEnvironmentVariable($_) })
        Assert-True -Condition ($notRestored.Count -eq 0) -Message "Package validation did not restore absent environment variables: $($notRestored -join ', ')."
    }
    finally {
        foreach ($name in $environmentNames) {
            if ($null -eq $originalEnvironment[$name]) {
                if (Test-Path -LiteralPath "Env:$name") {
                    Remove-Item -LiteralPath "Env:$name"
                }
            }
            else {
                [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name])
            }
        }
        if (Test-Path -LiteralPath $packagingTestRoot) {
            Remove-Item -LiteralPath $packagingTestRoot -Recurse -Force
        }
    }
}

Write-Host "PowerShell portability regression tests passed."
