using System.Runtime.Versioning;
using Microsoft.Win32;
using PwdVault.Core;

namespace PwdVault.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSecurity : IPlatformSecurity
{
    private readonly WindowsClipboard _clipboard;
    private bool _disposed;
    public event Action? LockRequested;

    public WindowsSecurity(nint window)
    {
        _clipboard = new WindowsClipboard(window, new Win32ClipboardApi());
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerChanged;
    }

    public void CopyPassword(string password)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _clipboard.CopyPassword(password);
    }

    public ClipboardClearResult ClearOwnedPassword() => _clipboard.ClearOwnedPassword();

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            LockRequested?.Invoke();
    }

    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume) LockRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        ClearOwnedPassword();
    }
}
