[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Task
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    $gitRoot = & git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and $gitRoot) {
        return [IO.Path]::GetFullPath($gitRoot.Trim())
    }

    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
}

function Normalize-Text {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }

    return (($Value.ToLowerInvariant() -replace '[^a-z0-9/\.\- ]', ' ') -replace '\s+', ' ').Trim()
}

function Get-SearchTokens {
    param([string]$Value)

    $stopWords = @(
        'the', 'and', 'for', 'with', 'from', 'into', 'this', 'that', 'then',
        'than', 'after', 'before', 'about', 'read', 'reads', 'reading', 'fix', 'bug'
    )

    $normalized = Normalize-Text $Value
    if (-not $normalized) {
        return @()
    }

    return @(
        $normalized -split ' ' |
            Where-Object { $_.Length -ge 3 -and $stopWords -notcontains $_ } |
            Select-Object -Unique
    )
}

function Should-IgnorePath {
    param([string]$RelativePath)

    $path = $RelativePath.Replace('\', '/')

    if ($path -match '^(artifacts/|TestResults/|\.vs/|Visual Studio 18/)') {
        return $true
    }

    if ($path -match '(^|/)(bin|obj)/') {
        return $true
    }

    if ($path -match '^\.synapse/(graph\.json|memories\.md|synapse-memory\.json)$') {
        return $true
    }

    if ($path -match '^\.codex/atlas/indexes/.*\.json$') {
        return $true
    }

    if ($path -match '\.nupkg$') {
        return $true
    }

    return $false
}

function Get-RelativePath {
    param(
        [string]$RepoRoot,
        [string]$FullPath
    )

    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\')
    $path = [IO.Path]::GetFullPath($FullPath)

    if ($path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        return $path.Substring($root.Length).TrimStart('\').Replace('\', '/')
    }

    return $FullPath.Replace('\', '/')
}

function Add-Unique {
    param(
        [System.Collections.Generic.List[string]]$List,
        [string]$Value
    )

    if (-not $Value) {
        return
    }

    if (-not $List.Contains($Value)) {
        $List.Add($Value)
    }
}

function Parse-FeatureCard {
    param(
        [string]$RepoRoot,
        [string]$Path
    )

    $title = ''
    $section = ''
    $taskKeywords = New-Object System.Collections.Generic.List[string]
    $importantFiles = New-Object System.Collections.Generic.List[string]
    $nearestTests = New-Object System.Collections.Generic.List[string]

    foreach ($line in (Get-Content -LiteralPath $Path)) {
        if (-not $title -and $line -match '^#\s+(.+)$') {
            $title = $Matches[1].Trim()
            continue
        }

        if ($line -match '^##\s+(.+)$') {
            $section = $Matches[1].Trim().ToLowerInvariant()
            continue
        }

        if ($line -notmatch '^\s*-\s+(.+?)\s*$') {
            continue
        }

        $value = $Matches[1].Trim()
        if ($value.StartsWith('`') -and $value.EndsWith('`') -and $value.Length -ge 2) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        switch ($section) {
            'task keywords' { Add-Unique -List $taskKeywords -Value $value }
            'important files' { Add-Unique -List $importantFiles -Value $value }
            'nearest tests' { Add-Unique -List $nearestTests -Value $value }
        }
    }

    [PSCustomObject]@{
        Path = Get-RelativePath -RepoRoot $RepoRoot -FullPath $Path
        Title = $title
        TaskKeywords = @($taskKeywords)
        ImportantFiles = @($importantFiles)
        NearestTests = @($nearestTests)
    }
}

$repoRoot = Get-RepoRoot
$atlasRoot = Join-Path $repoRoot '.codex\atlas'
$featureCardDir = Join-Path $atlasRoot 'feature-cards'

if (-not (Test-Path -LiteralPath $featureCardDir)) {
    throw "Missing feature-card directory: $featureCardDir"
}

Push-Location $repoRoot
try {
    $taskNormalized = Normalize-Text $Task
    $taskTokens = Get-SearchTokens $Task

    $featureCards = @(
        Get-ChildItem -LiteralPath $featureCardDir -Filter '*.md' -File |
            Where-Object { $_.Name -ne 'README.md' } |
            ForEach-Object { Parse-FeatureCard -RepoRoot $repoRoot -Path $_.FullName }
    )

    $cardMatches = @(
        @(
            foreach ($card in $featureCards) {
                $score = 0
                $matched = New-Object System.Collections.Generic.List[string]

                foreach ($phrase in @($card.Title) + $card.TaskKeywords) {
                    $normalizedPhrase = Normalize-Text $phrase
                    if (-not $normalizedPhrase) {
                        continue
                    }

                    $phraseTokens = Get-SearchTokens $phrase
                    if ($taskNormalized.Contains($normalizedPhrase)) {
                        $score += 5 + [Math]::Min($phraseTokens.Count, 2)
                        Add-Unique -List $matched -Value $phrase
                        continue
                    }

                    $overlap = @($phraseTokens | Where-Object { $taskTokens -contains $_ })
                    if ($overlap.Count -gt 0) {
                        $score += $overlap.Count
                        Add-Unique -List $matched -Value $phrase
                    }
                }

                if ($score -gt 0) {
                    [PSCustomObject]@{
                        Score = $score
                        Matched = @($matched)
                        Card = $card
                    }
                }
            }
        ) | Sort-Object -Property @{ Expression = 'Score'; Descending = $true }, @{ Expression = { $_.Card.Title }; Descending = $false }
    )

    $gitFiles = @(
        & git ls-files --cached --others --exclude-standard 2>$null |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not (Should-IgnorePath $_) } |
            Sort-Object -Unique
    )

    $fileMatches = @(
        @(
            foreach ($path in $gitFiles) {
                $score = 0
                $normalizedPath = $path.ToLowerInvariant()

                foreach ($token in $taskTokens) {
                    if ($normalizedPath.Contains($token)) {
                        $score++
                    }
                }

                if ($score -gt 0) {
                    [PSCustomObject]@{
                        Path = $path
                        Score = $score
                    }
                }
            }
        ) | Sort-Object -Property @{ Expression = 'Score'; Descending = $true }, @{ Expression = 'Path'; Descending = $false }
    )

    $matchedTests = New-Object System.Collections.Generic.List[string]
    foreach ($match in ($cardMatches | Select-Object -First 3)) {
        foreach ($testPath in $match.Card.NearestTests) {
            Add-Unique -List $matchedTests -Value $testPath
        }
    }

    foreach ($path in ($fileMatches.Path | Where-Object { $_ -like 'tests/*' } | Select-Object -First 6)) {
        Add-Unique -List $matchedTests -Value $path
    }

    $readOrder = New-Object System.Collections.Generic.List[string]
    Add-Unique -List $readOrder -Value '.codex/atlas/repo-map.md'

    foreach ($match in ($cardMatches | Select-Object -First 3)) {
        Add-Unique -List $readOrder -Value $match.Card.Path
    }

    if ($matchedTests.Count -gt 0) {
        Add-Unique -List $readOrder -Value '.codex/atlas/test-index.md'
    }

    foreach ($testPath in ($matchedTests | Select-Object -First 5)) {
        Add-Unique -List $readOrder -Value $testPath
    }

    foreach ($match in ($cardMatches | Select-Object -First 3)) {
        foreach ($filePath in $match.Card.ImportantFiles) {
            Add-Unique -List $readOrder -Value $filePath
        }
    }

    foreach ($path in ($fileMatches.Path | Where-Object { $_ -notlike 'tests/*' } | Select-Object -First 6)) {
        Add-Unique -List $readOrder -Value $path
    }

    Write-Output "Task: $Task"
    Write-Output ''
    Write-Output 'Likely feature cards:'
    if ($cardMatches.Count -eq 0) {
        Write-Output '- (none)'
    }
    else {
        foreach ($match in ($cardMatches | Select-Object -First 3)) {
            $matchedText = ($match.Matched | Select-Object -First 3) -join ', '
            Write-Output "- $($match.Card.Title) [$matchedText]"
        }
    }

    Write-Output ''
    Write-Output 'Matching filenames:'
    if ($fileMatches.Count -eq 0) {
        Write-Output '- (none)'
    }
    else {
        foreach ($file in ($fileMatches | Select-Object -First 6)) {
            Write-Output "- $($file.Path)"
        }
    }

    Write-Output ''
    Write-Output 'Matching tests:'
    if ($matchedTests.Count -eq 0) {
        Write-Output '- (none)'
    }
    else {
        foreach ($path in ($matchedTests | Select-Object -First 5)) {
            Write-Output "- $path"
        }
    }

    Write-Output ''
    Write-Output 'Suggested read order:'
    foreach ($path in ($readOrder | Select-Object -First 10)) {
        Write-Output "- $path"
    }
}
finally {
    Pop-Location
}
