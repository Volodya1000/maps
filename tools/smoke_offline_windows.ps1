[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApplicationDirectory,
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
$application = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$evidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$stdout = Join-Path $evidence 'stdout.txt'
$stderr = Join-Path $evidence 'stderr.txt'
$timeoutSeconds = 180
$previousData = $env:CABLEROUTE_DATA
$process = $null
try {
    Remove-Item Env:CABLEROUTE_DATA -ErrorAction SilentlyContinue
    # Launch the extracted GUI app with its own Data directory and runtime.
    $process = Start-Process -FilePath (Join-Path $application 'CableRoutePlanner.Desktop.exe') `
        -WorkingDirectory $application -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        Start-Sleep -Seconds 2
        $process.Refresh()
        if ($process.HasExited) {
            throw "Extracted desktop exited with code $($process.ExitCode). See $stderr"
        }
        $ready = Select-String -LiteralPath $stdout -Pattern 'CRP_OFFLINE_MAP_READY region=belarus features=[1-9][0-9]*' -Quiet
    } until ($ready -or [DateTime]::UtcNow -gt $deadline)
    if (-not $ready) { throw "Extracted desktop did not open its bundled full Belarus map within $timeoutSeconds seconds." }
    Get-Content -LiteralPath $stdout
} finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction Stop }
        $process.Dispose()
    }
    if ($null -ne $previousData) { $env:CABLEROUTE_DATA = $previousData }
}
