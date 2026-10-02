param([string]$InnoCompiler, [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = $PSScriptRoot
Set-Location -LiteralPath $projectRoot
function Invoke-Checked([string]$Command, [string[]]$Arguments) { & $Command @Arguments; if ($LASTEXITCODE -ne 0) { throw "$Command failed with code $LASTEXITCODE" } }
function Safe-Clean([string]$Relative) {
    $target = [IO.Path]::GetFullPath((Join-Path $projectRoot $Relative))
    if (-not $target.StartsWith($projectRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe clean target.' }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
Safe-Clean 'artifacts\release'
Safe-Clean 'artifacts\service-publish'
Safe-Clean 'dist'
New-Item -ItemType Directory -Path artifacts\release,artifacts\service-publish,dist,core,.tools -Force | Out-Null
$pin = Get-Content -LiteralPath core\version.json -Raw | ConvertFrom-Json
$archive = Join-Path $projectRoot '.tools\sing-box.zip'
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $pin.sha256) {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest ('https://github.com/SagerNet/sing-box/releases/download/v' + $pin.version + '/' + $pin.archive) -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $pin.sha256) { throw 'sing-box archive checksum mismatch.' }
Safe-Clean '.tools\sing-box-extracted'
Expand-Archive -LiteralPath $archive -DestinationPath .tools\sing-box-extracted -Force
$coreFolder = Join-Path $projectRoot ('.tools\sing-box-extracted\sing-box-' + $pin.version + '-windows-amd64')
Copy-Item -LiteralPath (Join-Path $coreFolder 'sing-box.exe') -Destination core\sing-box.exe -Force
Copy-Item -LiteralPath (Join-Path $coreFolder 'LICENSE') -Destination core\LICENSE -Force
Invoke-Checked dotnet @('clean','Bebekon.sln','-c','Release','--nologo','-v','quiet')
Invoke-Checked dotnet @('restore','Bebekon.sln','--nologo')
Invoke-Checked dotnet @('test','tests\Bebekon.Tests','-c','Release','--nologo','--logger','trx;LogFileName=core.trx','--results-directory','artifacts\tests')
$publishArgs = @('-c','Release','-r','win-x64','--self-contained','true','-p:PublishReadyToRun=true','-p:PublishSingleFile=false','--nologo')
Invoke-Checked dotnet (@('publish','src\Bebekon.App\Bebekon.App.csproj') + $publishArgs + @('-o','artifacts\release'))
Invoke-Checked dotnet (@('publish','src\Bebekon.Service\Bebekon.Service.csproj') + $publishArgs + @('-o','artifacts\service-publish'))
# Desktop runtime assemblies (especially WindowsBase.dll) must never be replaced by
# the forwarding stubs in the plain .NET service runtime.
$servicePublishRoot = Join-Path $projectRoot 'artifacts\service-publish'
$appPublishRoot = Join-Path $projectRoot 'artifacts\release'
foreach ($file in Get-ChildItem -LiteralPath $servicePublishRoot -File -Recurse) {
    $relative = $file.FullName.Substring($servicePublishRoot.Length + 1)
    $destination = Join-Path $appPublishRoot $relative
    if (-not (Test-Path -LiteralPath $destination)) { New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null; Copy-Item -LiteralPath $file.FullName -Destination $destination }
}
Copy-Item -LiteralPath scripts -Destination artifacts\release -Recurse -Force
Copy-Item -LiteralPath README.md,ARCHITECTURE.md,LICENSE -Destination artifacts\release -Force
New-Item -ItemType Directory -Path artifacts\release\docs -Force | Out-Null
Copy-Item -LiteralPath docs\VALIDATION.md -Destination artifacts\release\docs -Force
Copy-Item -LiteralPath core\LICENSE,core\version.json -Destination artifacts\release\core -Force
if (-not $SkipInstaller) {
    if (-not $InnoCompiler) {
        foreach ($candidate in @((Join-Path $projectRoot '.tools\InnoSetup\ISCC.exe'), 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 7\ISCC.exe')) { if (Test-Path -LiteralPath $candidate) { $InnoCompiler=$candidate; break } }
    }
    if (-not $InnoCompiler) {
        $setup = Join-Path $projectRoot '.tools\innosetup.exe'
        Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $setup
        if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732') { throw 'Inno Setup checksum mismatch.' }
        # Per-user compiler only; installing the VPN service is done later by Setup with UAC.
        $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="' + (Join-Path $projectRoot '.tools\InnoSetup') + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -ne 0) { throw 'Could not install the build compiler.' }
        $InnoCompiler = Join-Path $projectRoot '.tools\InnoSetup\ISCC.exe'
    }
    Invoke-Checked $InnoCompiler @('/Qp',('/DPublishDir=' + (Join-Path $projectRoot 'artifacts\release')),('/DOutputDir=' + (Join-Path $projectRoot 'dist')),'installer\setup.iss')
}
Compress-Archive -Path artifacts\release\* -DestinationPath dist\BebekonVPN-Portable-x64.zip -CompressionLevel Optimal
Get-ChildItem -LiteralPath dist | Select-Object Name,Length
