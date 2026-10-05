param([string]$GoExecutable = 'go', [string]$SingBoxSource, [string]$XraySource)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$pins = Get-Content -LiteralPath (Join-Path $projectRoot 'core/security-pins.json') -Raw | ConvertFrom-Json
function Invoke-Go([string[]]$Arguments) { & $GoExecutable @Arguments; if ($LASTEXITCODE -ne 0) { throw 'Secure native core build failed.' } }
$goVersion = (& $GoExecutable version) -split ' '
if ([version]$goVersion[2].Replace('go','') -lt [version]$pins.goVersion) { throw 'Use the pinned Go version or a newer security patch.' }
$originalCGO = $env:CGO_ENABLED; $originalProcs = $env:GOMAXPROCS
try {
    $env:CGO_ENABLED = '0'; $env:GOMAXPROCS = '2'
    foreach ($coreName in @('sing-box','xray')) {
        $source = if ($coreName -eq 'sing-box') { $SingBoxSource } else { $XraySource }
        if (-not $source) { $source = Join-Path $projectRoot ('.tools/security/source-' + $coreName) }
        $repository = if ($coreName -eq 'sing-box') { 'https://github.com/SagerNet/sing-box.git' } else { 'https://github.com/XTLS/Xray-core.git' }
        $release = if ($coreName -eq 'sing-box') { '1.14.2' } else { (Get-Content (Join-Path $projectRoot 'core/xray-version.json') -Raw | ConvertFrom-Json).version }
        if (-not (Test-Path -LiteralPath $source)) { & git clone --depth 1 --branch ('v' + $release) $repository $source; if ($LASTEXITCODE -ne 0) { throw 'Native core source download failed.' } }
        $expected = if ($coreName -eq 'sing-box') { $pins.singBoxCommit } else { $pins.xrayCommit }
        if ((& git -C $source rev-parse HEAD).Trim() -ne $expected) { throw 'Native core source commit does not match the pin.' }
        Push-Location -LiteralPath $source
        try {
            Invoke-Go (@('get') + @($pins.modules))
            $arguments = @('build', '-trimpath', '-buildvcs=false', '-ldflags', '-s -w -checklinkname=0 -buildid=', '-o', (Join-Path $projectRoot ('core/' + $coreName + '.exe')))
            if ($coreName -eq 'sing-box') { $arguments[4] += ' -X github.com/sagernet/sing-box/constant.Version=1.14.2'; $arguments += @('-tags', $pins.singBoxTags, './cmd/sing-box') } else { $arguments += './main' }
            Invoke-Go $arguments
        } finally { Pop-Location }
    }
} finally { $env:CGO_ENABLED = $originalCGO; $env:GOMAXPROCS = $originalProcs }
