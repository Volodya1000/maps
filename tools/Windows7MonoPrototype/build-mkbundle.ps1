param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/windows7-mono-x86'
)

$ErrorActionPreference = 'Stop'

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found. Install Mono for Windows 6.12 x86 and add its bin directory to PATH."
    }
}

Require-Command 'dotnet'
Require-Command 'mkbundle'

$monoBin = Split-Path (Get-Command 'mkbundle').Source -Parent
$monoRoot = Split-Path $monoBin -Parent
$monoFramework = Join-Path $monoRoot 'lib/mono/4.8-api'
$monoFacades = Join-Path $monoFramework 'Facades'
$project = Join-Path $PSScriptRoot 'Windows7MonoPrototype.csproj'
$buildOutput = Join-Path $PSScriptRoot "bin/$Configuration/net48"
$output = [IO.Path]::GetFullPath($OutputDirectory)

dotnet restore $project --locked-mode
dotnet build $project --no-restore -c $Configuration --nologo -p:TreatWarningsAsErrors=true

New-Item -ItemType Directory -Force -Path $output | Out-Null
$nativeAssemblyNames = @('av_libglesv2.dll', 'e_sqlite3.dll', 'libHarfBuzzSharp.dll', 'libSkiaSharp.dll')
$managedAssemblies = Get-ChildItem $buildOutput -Filter '*.dll' -File |
    Where-Object { $_.Name -notin $nativeAssemblyNames } |
    ForEach-Object FullName
$entryPoint = Join-Path $buildOutput 'Windows7MonoPrototype.exe'
$bundle = Join-Path $output 'Windows7MonoPrototype.exe'

if (-not (Test-Path $entryPoint)) {
    throw "The managed prototype was not built: $entryPoint"
}

$arguments = @(
    '--simple',
    '--static',
    '-L', $buildOutput,
    '-L', $monoFramework,
    '-L', $monoFacades,
    '-o', $bundle,
    $entryPoint
) + $managedAssemblies
& mkbundle @arguments
if ($LASTEXITCODE -ne 0) {
    throw "mkbundle failed with exit code $LASTEXITCODE. See the Mono diagnostics above."
}

foreach ($nativeName in $nativeAssemblyNames) {
    $native = Join-Path $buildOutput $nativeName
    if (Test-Path $native) {
        Copy-Item $native $output -Force
    }
}

Copy-Item (Join-Path $PSScriptRoot 'README.md') $output -Force
Write-Host "Mono bundle candidate: $bundle"
Write-Host 'Native graphics and SQLite libraries remain beside the bundle; they are not .NET runtime files.'
