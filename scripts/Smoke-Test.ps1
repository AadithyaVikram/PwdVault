param([string]$Configuration = 'Release', [string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $workspace "src/PwdVault.Desktop/bin/$Configuration/net10.0/PwdVault.Desktop.exe"
if ($ExecutablePath) { $executable = [System.IO.Path]::GetFullPath($ExecutablePath) }
$artifacts = Join-Path $workspace 'artifacts'
[System.IO.Directory]::CreateDirectory($artifacts) | Out-Null
$errorFile = Join-Path $artifacts 'desktop-smoke.stderr.txt'
$process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru -WindowStyle Hidden -RedirectStandardError $errorFile
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        [System.Threading.Thread]::Sleep(200)
        $process.Refresh()
        if ($process.HasExited) { throw "Desktop exited before opening a window. See $errorFile" }
    } while ($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'No desktop window appeared.' }
    [System.Threading.Thread]::Sleep(800)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $nodes = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $names = @()
    foreach ($node in $nodes) {
        try { $names += $node.Current.Name } catch { }
    }
    foreach ($expected in @('Choose existing vault', 'Choose new location', 'Unlock vault', 'PwdVault logo')) {
        if ($names -notcontains $expected) { throw "Missing expected UI control: $expected" }
    }
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PwdVaultSmokeCapture {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
}
'@
    $rect = New-Object PwdVaultSmokeCapture+Rect
    [PwdVaultSmokeCapture]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc = $graphics.GetHdc()
            try { [PwdVaultSmokeCapture]::PrintWindow($process.MainWindowHandle, $dc, 2) | Out-Null }
            finally { $graphics.ReleaseHdc($dc) }
        } finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $artifacts 'desktop-startup.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    Write-Output "Desktop startup passed: $($process.MainWindowTitle); create/open/unlock controls found."
} finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id }
    }
    $process.Dispose()
}
