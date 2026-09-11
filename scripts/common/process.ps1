# Native-process execution shared by packaging and command tests.
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
    # Let .NET encode each argument for the platform, preserving empty values and embedded quotes.
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

        # Drain both pipes while the command runs so a full pipe cannot block it from exiting.
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $timedOut = $false
        if ($TimeoutSeconds -eq 0)
        {
            # Packaging operations can opt out of the command-test timeout.
            $process.WaitForExit()
        }
        elseif (-not $process.WaitForExit($TimeoutSeconds * 1000))
        {
            $timedOut = $true
            try
            {
                # Include child processes so a timed-out command does not leave build workers running.
                $process.Kill($true)
            }
            catch [System.InvalidOperationException]
            {
                # The process may finish between the timeout check and Kill.
                if (-not $process.HasExited)
                {
                    throw
                }
            }

            $process.WaitForExit()
        }

        # Return failures as data so the command suite can finish collecting results before failing.
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

    # Successful output stays on the verbose stream to keep the caller's return values clean.
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
    # This is a PowerShell-readable diagnostic; process execution uses the original argument array.
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
