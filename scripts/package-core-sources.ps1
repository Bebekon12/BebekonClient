param([string]$SingBoxSource, [string]$XraySource, [string]$TrustTunnelSource,
    [string]$OutputFile = 'dist/Bebekon-Core-Sources.zip')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SingBoxSource) { $SingBoxSource = Join-Path $projectRoot '.tools/android/sing-box' }
if (-not $XraySource) { $XraySource = Join-Path $projectRoot '.tools/security/source-xray' }
if (-not $TrustTunnelSource) { $TrustTunnelSource = Join-Path $projectRoot '.tools/trusttunnel/source' }
$pins = Get-Content -LiteralPath (Join-Path $projectRoot 'core/security-pins.json') -Raw | ConvertFrom-Json
if ((& git -C $SingBoxSource rev-parse HEAD).Trim() -ne $pins.singBoxCommit -or
    (& git -C $XraySource rev-parse HEAD).Trim() -ne $pins.xrayCommit) { throw 'Unexpected core source commit.' }
$trustVersion = (Get-Content -LiteralPath (Join-Path $projectRoot 'core/trusttunnel-version.json') -Raw | ConvertFrom-Json).version
if ((& git -C $TrustTunnelSource describe --tags --exact-match HEAD).Trim() -ne ('v' + $trustVersion)) { throw 'Unexpected TrustTunnel source tag.' }
Add-Type -AssemblyName System.IO.Compression
$temporary = Join-Path $projectRoot ('.tools/security/source-package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputFile))
New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force | Out-Null
try {
    $outputStream = [IO.File]::Open($outputPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $output = [IO.Compression.ZipArchive]::new($outputStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in @(@('sing-box',$SingBoxSource), @('xray',$XraySource), @('trusttunnel',$TrustTunnelSource))) {
            $name = $item[0]; $source = $item[1]; $archive = Join-Path $temporary ($name + '.zip')
            & git -C $source archive --format=zip ('--output=' + $archive) HEAD
            if ($LASTEXITCODE -ne 0) { throw 'Source export failed.' }
            $replacement = if ($name -eq 'sing-box') { @('go.mod','go.sum','experimental/libbox/bebekon.go') } elseif ($name -eq 'xray') { @('go.mod','go.sum') } else { @() }
            $input = [IO.Compression.ZipFile]::OpenRead($archive)
            try {
                foreach ($entry in $input.Entries) {
                    if ($entry.FullName.EndsWith('/') -or $entry.FullName -in $replacement) { continue }
                    $destination = $output.CreateEntry($name + '/' + $entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
                    $from = $entry.Open(); $to = $destination.Open()
                    try { $from.CopyTo($to) } finally { $to.Dispose(); $from.Dispose() }
                }
            } finally { $input.Dispose() }
            foreach ($relative in $replacement) {
                $file = if ($relative -eq 'experimental/libbox/bebekon.go') { Join-Path $projectRoot 'android/core/bebekon.go' } else { Join-Path $source $relative }
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($output, $file, ($name + '/' + $relative)) | Out-Null
            }
        }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($output, (Join-Path $projectRoot 'core/security-pins.json'), 'security-pins.json') | Out-Null
    } finally { $output.Dispose() }
} finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.tools/security')).TrimEnd('\') + '\'
    if (-not $resolvedTemporary.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary directory.' }
    Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
}
Get-FileHash -LiteralPath $outputPath -Algorithm SHA256
