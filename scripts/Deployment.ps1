# Shared per-user deployment helpers. App versions are immutable; vaults are never package files.
Set-StrictMode -Version Latest
function Resolve-PwdVaultRoot([string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]"\/")
    if ([IO.Path]::GetFileName($resolved) -ne 'PwdVault') { throw 'The install directory must be a dedicated folder named PwdVault.' }
    if ((Test-Path -LiteralPath $resolved) -and ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'A linked install directory is not supported.'
    }
    return $resolved
}
function Resolve-PwdVaultChild([string]$Root, [string]$Relative) {
    $target = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $target.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Deployment path escapes the dedicated install directory.'
    }
    $ancestor = $target
    while ($ancestor -and $ancestor.Length -gt $Root.Length) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked deployment paths are not supported.'
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $target
}
function Get-PwdVaultState([string]$Root) {
    $file = Join-Path $Root 'install-state.json'
    if (Test-Path -LiteralPath $file) {
        $state = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        if ($state.Schema -ne 1) { throw 'Unsupported installation metadata.' }
        Resolve-PwdVaultChild $Root $state.Active | Out-Null
        if ($state.Previous) { Resolve-PwdVaultChild $Root $state.Previous | Out-Null }
        return $state
    }
    return $null
}
function Assert-PwdVaultNotRunning([string]$Root) {
    foreach ($process in (Get-Process -Name PwdVault.Desktop -ErrorAction SilentlyContinue)) {
        if (-not $process.Path) { throw 'Could not inspect an existing PwdVault process. Close it before updating.' }
        if ($process.Path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Close the installed PwdVault window before changing its installation.'
        }
    }
}
function Get-PwdVaultInventory([string]$Directory) {
    $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd([char[]]"\/") + [IO.Path]::DirectorySeparatorChar
    $items = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Packages containing links are not supported.'
    }
    return @($items | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length)
        if ($relative -match '(?i)\.pwdvault($|\.)|\.(pfx|p12|key)$|(^|[\\/])\.env($|\.)') {
            throw 'A package contains vault or credential files. Installation stopped.'
        }
        [pscustomobject]@{ Path = $relative; Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    } | Sort-Object Path)
}
function Assert-PwdVaultVersion([string]$Directory) {
    $manifestFile = Join-Path $Directory 'package-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.Files) {
        $file = Resolve-PwdVaultChild $Directory $entry.Path
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Hash) {
            throw 'The installed package is incomplete or changed. Activation stopped.'
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'PwdVault.Desktop.exe') -PathType Leaf)) { throw 'Missing app executable.' }
}
function Replace-PwdVaultFile([string]$Source, [string]$Destination, [string]$Backup) {
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            if ($Backup) { [IO.File]::Replace($Source, $Destination, $Backup) }
            else { [IO.File]::Replace($Source, $Destination, [Management.Automation.Language.NullString]::Value) }
            return
        } catch {
            $failure = $_.Exception
            if ($failure.InnerException) { $failure = $failure.InnerException }
            $code = $failure.HResult -band 0xffff
            if ($attempt -ge 9 -or $code -notin @(32, 33, 1175)) { throw }
            # A scanner or shell extension can briefly hold a newly created shortcut.
            [Threading.Thread]::Sleep(100)
        }
    }
}
function Set-PwdVaultActive([string]$Root, [string]$Active, [string]$Previous, [string]$Desktop, [string]$Programs) {
    $version = Resolve-PwdVaultChild $Root $Active
    Assert-PwdVaultVersion $version
    $exe = Join-Path $version 'PwdVault.Desktop.exe'
    $shortcuts = @()
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($location in @($Desktop, $Programs)) {
            if (-not $location) { throw 'Missing Windows shortcut location.' }
            [IO.Directory]::CreateDirectory($location) | Out-Null
            $destination = Join-Path $location 'PwdVault.lnk'
            if (Test-Path -LiteralPath $destination -PathType Container) { throw 'A folder is blocking a shortcut.' }
            $old = if (Test-Path -LiteralPath $destination) { [IO.File]::ReadAllBytes($destination) } else { $null }
            $temp = Join-Path $location ('PwdVault.pending-' + [Guid]::NewGuid().ToString('N') + '.lnk')
            $shortcuts += [pscustomobject]@{ Path = $destination; Temporary = $temp; Old = $old; Changed = $false }
            $link = $shell.CreateShortcut($temp)
            try {
                $link.TargetPath = $exe; $link.WorkingDirectory = $version
                $link.IconLocation = "$exe,0"; $link.Description = 'PwdVault - offline password vault'
                $link.Save()
            } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
        }
        foreach ($shortcut in $shortcuts) {
            if (Test-Path -LiteralPath $shortcut.Path) {
                Replace-PwdVaultFile $shortcut.Temporary $shortcut.Path
            } else { [IO.File]::Move($shortcut.Temporary, $shortcut.Path) }
            $shortcut.Changed = $true
        }
        $state = [pscustomobject]@{ Schema = 1; Active = $Active; Previous = $Previous; ActivatedUtc = [DateTime]::UtcNow.ToString('o') }
        $tempState = Join-Path $Root ('state-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        $stateFile = Join-Path $Root 'install-state.json'
        $stateBytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($state | ConvertTo-Json))
        $stateStream = New-Object IO.FileStream($tempState, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try { $stateStream.Write($stateBytes, 0, $stateBytes.Length); $stateStream.Flush($true) }
        finally { $stateStream.Dispose() }
        try {
            if (Test-Path -LiteralPath $stateFile) { Replace-PwdVaultFile $tempState $stateFile (Join-Path $Root 'install-state.previous.json') }
            else { [IO.File]::Move($tempState, $stateFile) }
        } finally { if (Test-Path -LiteralPath $tempState) { Remove-Item -LiteralPath $tempState } }
    } catch {
        foreach ($shortcut in $shortcuts) {
            if ($shortcut.Changed) {
                if ($null -ne $shortcut.Old) { [IO.File]::WriteAllBytes($shortcut.Path, $shortcut.Old) }
                elseif (Test-Path -LiteralPath $shortcut.Path) { Remove-Item -LiteralPath $shortcut.Path }
            }
        }
        throw
    } finally {
        foreach ($shortcut in $shortcuts) {
            if (Test-Path -LiteralPath $shortcut.Temporary) { Remove-Item -LiteralPath $shortcut.Temporary }
        }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
}
