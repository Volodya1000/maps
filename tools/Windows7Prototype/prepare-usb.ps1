param([Parameter(Mandatory = $true)][string]$Payload)
$ErrorActionPreference = 'Stop'
$specification = Get-Content (Join-Path $PSScriptRoot 'prerequisites.json') -Raw | ConvertFrom-Json
$prerequisites = Join-Path $Payload 'Prerequisites'
New-Item -ItemType Directory -Force -Path $prerequisites | Out-Null
$installer = Join-Path $prerequisites $specification.filename
Invoke-WebRequest -Uri $specification.url -OutFile $installer
$actual = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $specification.sha256) { throw 'The .NET Framework offline installer checksum does not match.' }
$signature = Get-AuthenticodeSignature $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw 'The .NET Framework installer does not have a valid Microsoft signature.'
}
Copy-Item (Join-Path $PSScriptRoot 'check-prerequisites.cmd') $Payload
Copy-Item (Join-Path $PSScriptRoot 'launch-on-laptop.cmd') $Payload
Copy-Item (Join-Path $PSScriptRoot 'prerequisites.json') $prerequisites
