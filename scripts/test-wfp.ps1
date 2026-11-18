param([switch]$PersistenceFixture)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run from an administrator PowerShell.'
    }
} finally { $identity.Dispose() }

$repo = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $repo 'Bebekon.sln'
$localSdk = Join-Path $repo '.tools\dotnet-10.0.401\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk -PathType Leaf) { $localSdk } else { 'dotnet' }
$helper = Join-Path $repo 'src\Bebekon.Service\bin\Release\net10.0-windows\Bebekon.Service.dll'
Push-Location -LiteralPath $repo
try {
    if (-not $PersistenceFixture) {
        if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) { throw 'Build the Release solution first.' }
        & $sdk $helper --test-wfp
        exit $LASTEXITCODE
    }

    $previous = [Environment]::GetEnvironmentVariable('BEBEKON_WFP_TEST_VM', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('BEBEKON_WFP_TEST_VM', '1', 'Process')
        & $sdk test $solution -c Release --no-build --filter 'Category=WfpKernel'
        exit $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable('BEBEKON_WFP_TEST_VM', $previous, 'Process')
    }
} finally { Pop-Location }
