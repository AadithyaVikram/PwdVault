# Isolated installer/rollback tests; no real shortcuts, apps, or vaults are changed.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$area = Join-Path $workspace ('artifacts/deployment-tests/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($area) | Out-Null
$root = Join-Path $area 'PwdVault'
$desktop = Join-Path $area 'Desktop'
$programs = Join-Path $area 'Programs'
$one = Join-Path $area 'package-one'; $two = Join-Path $area 'package-two'
foreach ($package in @($one, $two)) { [IO.Directory]::CreateDirectory($package) | Out-Null }
[IO.File]::WriteAllText((Join-Path $one 'PwdVault.Desktop.exe'), 'synthetic package one')
[IO.File]::WriteAllText((Join-Path $two 'PwdVault.Desktop.exe'), 'synthetic package two')
[IO.Directory]::CreateDirectory((Join-Path $one 'nested/runtime')) | Out-Null
[IO.File]::WriteAllText((Join-Path $one 'nested/runtime/synthetic.dat'), 'synthetic nested dependency')
$outsideVault = Join-Path $area 'do-not-change.pwdvault'
[IO.File]::WriteAllText($outsideVault, 'synthetic vault sentinel')
function Install([string]$Package) {
    & (Join-Path $PSScriptRoot 'Install-Local.ps1') -PublishDirectory $Package -InstallRoot $root -DesktopDirectory $desktop -ProgramsDirectory $programs
}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function State { return Get-Content -LiteralPath (Join-Path $root 'install-state.json') -Raw | ConvertFrom-Json }
Install $one
$first = (State).Active
$statePath = Join-Path $root 'install-state.json'
$beforeDesktop = (Get-FileHash -LiteralPath (Join-Path $desktop 'PwdVault.lnk')).Hash
$held = New-Object IO.FileStream($statePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $failed = $false
    try { Install $two } catch { $failed = $true }
    Assert $failed 'A locked activation state should prevent committing the update.'
} finally { $held.Dispose() }
Assert ((State).Active -eq $first) 'A failed activation changed the active version.'
Assert ((Get-FileHash -LiteralPath (Join-Path $desktop 'PwdVault.lnk')).Hash -eq $beforeDesktop) 'A failed activation did not restore the shortcut.'
Install $two
$second = (State).Active
Assert ($first -ne $second) 'Different packages must have different install directories.'
& (Join-Path $PSScriptRoot 'Rollback-Local.ps1') -InstallRoot $root -DesktopDirectory $desktop -ProgramsDirectory $programs
Assert ((State).Active -eq $first) 'Rollback did not select the previous package.'
# A corrupted existing version must never become active.
[IO.File]::WriteAllText((Join-Path (Join-Path $root $second) 'PwdVault.Desktop.exe'), 'corrupted')
$failed = $false
try { & (Join-Path $PSScriptRoot 'Rollback-Local.ps1') -InstallRoot $root -DesktopDirectory $desktop -ProgramsDirectory $programs } catch { $failed = $true }
Assert $failed 'Corrupted version was accepted for rollback.'
Assert ((State).Active -eq $first) 'A rejected rollback changed the active version.'
Assert ((Get-Content -LiteralPath $outsideVault -Raw) -eq 'synthetic vault sentinel') 'A test vault sentinel changed.'
Write-Output 'PASS: install, update, failed activation recovery, rollback, corrupted-package rejection, vault preservation.'
Write-Output "Synthetic deployment evidence retained at $area"

$insideVault = Join-Path (Join-Path $root $first) 'keep.pwdvault'
[IO.File]::WriteAllText($insideVault, 'synthetic vault inside install root')
& (Join-Path $PSScriptRoot 'Uninstall-Local.ps1') -InstallRoot $root -DesktopDirectory $desktop -ProgramsDirectory $programs
Assert ((Get-Content -LiteralPath $insideVault -Raw) -eq 'synthetic vault inside install root') 'Uninstall removed a vault inside the install folder.'
Assert ((Get-Content -LiteralPath $outsideVault -Raw) -eq 'synthetic vault sentinel') 'Uninstall changed an external vault.'
Assert (-not (Test-Path -LiteralPath (Join-Path $desktop 'PwdVault.lnk'))) 'Uninstall left its shortcut.'
Write-Output 'PASS: safe uninstall preserves vaults and unknown or modified files.'
Assert (-not (Test-Path -LiteralPath (Join-Path (Join-Path $root $first) 'nested'))) 'Uninstall left an empty managed subdirectory.'
