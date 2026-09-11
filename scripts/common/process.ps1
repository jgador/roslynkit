Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-CapturedProcess
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Arguments,
        [string]$WorkingDirectory = (Get-Location).ProviderPath,
        [ValidateRange(0, 1800)]
        [int]$TimeoutSeconds = 0
    )

    Write-Verbose (Format-Invocation -FilePath $FilePath -Arguments $Arguments)
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments)
    {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try
    {
        if (-not $process.Start())
        {
            throw "Unable to start process '$FilePath'."
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $timedOut = $false
        if ($TimeoutSeconds -eq 0)
        {
            $process.WaitForExit()
        }
        elseif (-not $process.WaitForExit($TimeoutSeconds * 1000))
        {
            $timedOut = $true
            try
            {
                $process.Kill($true)
            }
            catch [System.InvalidOperationException]
            {
                if (-not $process.HasExited)
                {
                    throw
                }
            }

            $process.WaitForExit()
        }

        return [pscustomobject]@{
            ExitCode = if ($timedOut) { -1 } else { $process.ExitCode }
            TimedOut = $timedOut
            StandardOutput = $standardOutputTask.GetAwaiter().GetResult()
            StandardError = $standardErrorTask.GetAwaiter().GetResult()
        }
    }
    finally
    {
        $process.Dispose()
    }
}

function Assert-ProcessSucceeded
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Result
    )

    if ($Result.TimedOut -or $Result.ExitCode -ne 0)
    {
        $reason = if ($Result.TimedOut) { "timed out" } else { "failed with exit code $($Result.ExitCode)" }
        throw "$Description $reason.`nstdout:`n$($Result.StandardOutput)`nstderr:`n$($Result.StandardError)"
    }

    foreach ($output in @($Result.StandardOutput, $Result.StandardError))
    {
        if (-not [string]::IsNullOrWhiteSpace($output))
        {
            Write-Verbose $output.TrimEnd()
        }
    }
}

function Format-Invocation
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Arguments
    )

    $quotedFilePath = "'$($FilePath.Replace("'", "''"))'"
    $quotedArguments = $Arguments | ForEach-Object { "'$($_.Replace("'", "''"))'" }
    return "& $quotedFilePath $($quotedArguments -join ' ')".TrimEnd()
}
