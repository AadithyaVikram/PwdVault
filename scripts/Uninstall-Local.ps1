param(
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/PwdVault'),
    [string]$DesktopDirectory = ([Environment]::GetFolderPath('DesktopDirectory')),
    [string]$ProgramsDirectory = ([Environment]::GetFolderPath('Programs'))
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.ps1')
$root = Resolve-PwdVaultRoot $InstallRoot
Assert-PwdVaultNotRunning $root
if (-not (Test-Path -LiteralPath $root)) { return }
$lease = New-Object IO.FileStream((Join-Path $root 'deployment.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($location in @($DesktopDirectory, $ProgramsDirectory)) {
            $path = Join-Path $location 'PwdVault.lnk'
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $link = $shell.CreateShortcut($path)
                try {
                    if ($link.TargetPath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $path }
                } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
            }
        }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    $apps = Resolve-PwdVaultChild $root 'apps'
    if (Test-Path -LiteralPath $apps) {
        foreach ($version in Get-ChildItem -LiteralPath $apps -Directory) {
            $directory = Resolve-PwdVaultChild $root ('apps/' + $version.Name)
            $manifestPath = Join-Path $directory 'package-manifest.json'
            if (-not (Test-Path -LiteralPath $manifestPath)) { continue }
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            foreach ($entry in $manifest.Files) {
                $file = Resolve-PwdVaultChild $directory $entry.Path
                if ($entry.Path -match '(?i)\.pwdvault($|\.)') { continue }
                if ((Test-Path -LiteralPath $file -PathType Leaf) -and
                    (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $entry.Hash) {
                    Remove-Item -LiteralPath $file
                }
            }
            # Remove only empty directories, never recursively remove an installation tree.
            foreach ($folder in @(Get-ChildItem -LiteralPath $directory -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending)) {
                $checked = Resolve-PwdVaultChild $directory $folder.FullName.Substring($directory.Length + 1)
                if (@(Get-ChildItem -LiteralPath $checked -Force).Count -eq 0) { Remove-Item -LiteralPath $checked }
            }
            if (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 1) {
                Remove-Item -LiteralPath $manifestPath
                Remove-Item -LiteralPath $directory
            }
        }
    }
    foreach ($name in @('install-state.json', 'install-state.previous.json')) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    Write-Output 'Removed managed app files and shortcuts. Vaults, modified files, legacy files, and unrecognized files were preserved.'
} finally { $lease.Dispose() }
