param(
    [string]$PublishDirectory,
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/PwdVault'),
    [string]$DesktopDirectory = ([Environment]::GetFolderPath('DesktopDirectory')),
    [string]$ProgramsDirectory = ([Environment]::GetFolderPath('Programs'))
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.ps1')
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $workspace 'artifacts/win-x64' }
$source = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd([char[]]"\/")
$root = Resolve-PwdVaultRoot $InstallRoot
if ($source -eq $root -or $source.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish outside the installation directory.'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'PwdVault.Desktop.exe') -PathType Leaf)) { throw 'Publish the Windows app before installing it.' }
Assert-PwdVaultNotRunning $root
[IO.Directory]::CreateDirectory($root) | Out-Null
$lease = New-Object IO.FileStream((Join-Path $root 'deployment.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $files = @(Get-PwdVaultInventory $source)
    if ($files.Count -eq 0) { throw 'Empty package.' }
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $identity = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes(($files | ConvertTo-Json -Compress)))).Replace('-', '').ToLowerInvariant()
    } finally { $hasher.Dispose() }
    $relative = 'apps/' + $identity.Substring(0, 24)
    $destination = Resolve-PwdVaultChild $root $relative
    if (-not (Test-Path -LiteralPath $destination)) {
        $stage = Resolve-PwdVaultChild $root ('.staging-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $stage -Recurse
        $manifest = [pscustomobject]@{ Schema = 1; Files = $files }
        [IO.File]::WriteAllText((Join-Path $stage 'package-manifest.json'), ($manifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
        Assert-PwdVaultVersion $stage
        [IO.Directory]::CreateDirectory((Join-Path $root 'apps')) | Out-Null
        # Both resolved paths were checked to remain inside the dedicated installation root.
        [IO.Directory]::Move($stage, $destination)
    }
    $state = Get-PwdVaultState $root
    $previous = if ($state -and $state.Active -ne $relative) { $state.Active } elseif ($state) { $state.Previous } else { '' }
    Set-PwdVaultActive $root $relative $previous $DesktopDirectory $ProgramsDirectory
    Write-Output "Installed and verified PwdVault: $destination"
    Write-Output 'Desktop and Start menu shortcuts now point to the complete versioned build.'
} finally { $lease.Dispose() }
