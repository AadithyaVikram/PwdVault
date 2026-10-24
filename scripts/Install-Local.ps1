# Installs the published build for the current Windows user; no administrator access needed.
param([string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $workspace 'artifacts/win-x64' }
$source = [System.IO.Path]::GetFullPath($PublishDirectory)
$sourceExe = Join-Path $source 'PwdVault.Desktop.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "Publish the Windows build to $source first. See README.md."
}
$install = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/PwdVault'
$targetExe = Join-Path $install 'PwdVault.Desktop.exe'
foreach ($process in (Get-Process -Name PwdVault.Desktop -ErrorAction SilentlyContinue)) {
    if ($process.Path -and [string]::Equals($process.Path, $targetExe, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Close the installed PwdVault window before updating it.'
    }
}
[System.IO.Directory]::CreateDirectory($install) | Out-Null
# All copied files come from the dedicated publish folder; vault files are never installed here.
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $install -Recurse -Force
$shell = New-Object -ComObject WScript.Shell
try {
    $locations = @(
        [Environment]::GetFolderPath('DesktopDirectory'),
        [Environment]::GetFolderPath('Programs')
    )
    foreach ($location in $locations) {
        if (-not $location) { throw 'Windows did not provide a shortcut location.' }
        [System.IO.Directory]::CreateDirectory($location) | Out-Null
        $shortcut = $shell.CreateShortcut((Join-Path $location 'PwdVault.lnk'))
        $shortcut.TargetPath = $targetExe
        $shortcut.WorkingDirectory = $install
        $shortcut.IconLocation = "$targetExe,0"
        $shortcut.Description = 'PwdVault - offline password vault'
        $shortcut.Save()
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
    }
} finally { [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
Write-Output "Installed PwdVault to $install"
Write-Output 'Created Desktop and Start menu shortcuts named PwdVault.'
