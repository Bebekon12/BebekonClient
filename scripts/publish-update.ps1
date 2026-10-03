param([switch]$InitializeKey, [string]$Installer, [string]$Version, [string]$Output,
      [string]$InstallerUrl, [string]$Notes = 'Улучшения и исправления Bebekon VPN.')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publicPath = Join-Path $projectRoot 'resources\updates\public-key.xml'
# The private release key stays outside the repository and is encrypted for this Windows user.
$keyFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BebekonReleaseKeys'
$keyPath = Join-Path $keyFolder 'release-key.dpapi'
Add-Type -AssemblyName System.Security
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(3072)
$rsa.PersistKeyInCsp = $false
try {
    if (Test-Path -LiteralPath $keyPath) {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        try { $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($bytes)) } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
    } elseif ($InitializeKey -and -not (Test-Path -LiteralPath $publicPath)) {
        New-Item -ItemType Directory -Path $keyFolder -Force | Out-Null
        $bytes = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
        try { [IO.File]::WriteAllBytes($keyPath, [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)) } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        New-Item -ItemType Directory -Path (Split-Path -Parent $publicPath) -Force | Out-Null
        [IO.File]::WriteAllText($publicPath, $rsa.ToXmlString($false))
    } else { throw 'Release signing key missing. Restore the original private key; never rotate it silently.' }
    if ($rsa.ToXmlString($false) -ne [IO.File]::ReadAllText($publicPath)) { throw 'Private key does not match the shipped public key.' }
    if ($InitializeKey) { return }
    $package = Get-Item -LiteralPath $Installer
    if (-not $InstallerUrl) { $InstallerUrl = $package.Name }
    $payload = [ordered]@{ version=$Version; installer=$InstallerUrl; size=$package.Length; sha256=(Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash; notes=$Notes } | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $envelope = [ordered]@{ payload=[Convert]::ToBase64String($bytes); signature=[Convert]::ToBase64String($rsa.SignData($bytes, 'SHA256')) } | ConvertTo-Json
    $outputPath = [IO.Path]::GetFullPath($Output)
    [IO.File]::WriteAllText($outputPath + '.new', $envelope)
    Move-Item -LiteralPath ($outputPath + '.new') -Destination $outputPath -Force
} finally { $rsa.Dispose() }
