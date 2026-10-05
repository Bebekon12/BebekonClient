$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/helper-path-security.ps1')
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('BebekonSecurity-' + [guid]::NewGuid().ToString('N'))))
$junctionPath = Join-Path $fixtureRoot 'junction'
function Expect-Rejection([scriptblock]$Action) { $rejected = $false; try { & $Action } catch { $rejected = $true }; if (-not $rejected) { throw 'Unsafe helper path was accepted.' } }
try {
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'actual/deep/package') -Force | Out-Null
    Assert-NoHelperReparsePath (Join-Path $fixtureRoot 'actual/deep/package')
    New-Item -ItemType Junction -Path $junctionPath -Target (Join-Path $fixtureRoot 'actual') | Out-Null
    Expect-Rejection { Assert-NoHelperReparsePath (Join-Path $junctionPath 'deep/package') }
    Expect-Rejection { Assert-TrustedHelperParents (Join-Path $fixtureRoot 'actual/deep/package') (Join-Path $fixtureRoot 'other') }
    # An ordinary-user-owned package must never be accepted for a LocalSystem binary.
    Expect-Rejection { Assert-TrustedHelperAcl (Join-Path $fixtureRoot 'actual') }
    Write-Output 'PASS: ordinary paths accepted; junction ancestors, foreign roots and user-owned service files rejected.'
} finally {
    # Remove the link itself before recursion; every deletion remains inside this fixture.
    if (Test-Path -LiteralPath $junctionPath) { (Get-Item -LiteralPath $junctionPath).Delete() }
    if (-not $fixtureRoot.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\BebekonSecurity-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
