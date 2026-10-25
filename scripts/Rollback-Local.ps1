param(
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/PwdVault'),
    [string]$DesktopDirectory = ([Environment]::GetFolderPath('DesktopDirectory')),
    [string]$ProgramsDirectory = ([Environment]::GetFolderPath('Programs'))
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.ps1')
$root = Resolve-PwdVaultRoot $InstallRoot
Assert-PwdVaultNotRunning $root
$lease = New-Object IO.FileStream((Join-Path $root 'deployment.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $state = Get-PwdVaultState $root
    if (-not $state -or -not $state.Previous) { throw 'No previous versioned installation is available.' }
    Set-PwdVaultActive $root $state.Previous $state.Active $DesktopDirectory $ProgramsDirectory
    Write-Output 'Rolled back both shortcuts to the verified previous app version. Vault files were not modified.'
} finally { $lease.Dispose() }
