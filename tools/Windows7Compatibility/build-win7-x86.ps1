[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = 'artifacts/windows7-x86',
    [Parameter()]
    [string]$GeodataDirectory = 'samples/real-geodata-fixtures/minsk'
)

$ErrorActionPreference = 'Stop'

$project = 'src/CableRoutePlanner.Desktop/CableRoutePlanner.Desktop.csproj'
$outputPath = Join-Path $PWD $OutputDirectory
$geodataPath = (Resolve-Path -LiteralPath $GeodataDirectory).Path

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Invoke-DotNet @('restore', $project, '--locked-mode', '-r', 'win-x86', `
    '-p:Win7Port=true', `
    '-p:PlatformTarget=x86', `
    '-p:NuGetLockFilePath=packages.win7.lock.json')

if (Test-Path $outputPath) {
    Remove-Item -Recurse -Force $outputPath
}

Invoke-DotNet @('publish', $project, '--no-restore', `
    '-c', 'Release', `
    '-f', 'net6.0', `
    '-r', 'win-x86', `
    '--self-contained', 'true', `
    '-p:Win7Port=true', `
    '-p:PlatformTarget=x86', `
    '-p:NuGetLockFilePath=packages.win7.lock.json', `
    '-p:PublishSingleFile=false', `
    "-p:GeodataDirectory=$geodataPath", `
    '-o', $outputPath)

$files = Get-ChildItem -Path $outputPath -File -Recurse | Sort-Object FullName
if ($files.Count -eq 0) {
    throw "The Windows 7 package is empty: $outputPath"
}

$checksums = foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($outputPath.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
    "{0} *{1}" -f (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relativePath.Replace('\', '/')
}
$checksums | Set-Content -Path (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding utf8
