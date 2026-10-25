using System.Security.Cryptography;
using System.Text;
using PwdVault.Core;

namespace PwdVault.Platform.Windows;

// The small native boundary allows policy tests without touching the user's clipboard.
public interface IWindowsClipboardApi
{
    uint Register(string name);
    bool Open(nint window);
    void Close();
    bool Empty();
    nint Owner { get; }
    uint Sequence { get; }
    void Put(uint format, byte[] bytes);
    byte[]? ReadMarker(uint format);
}

public sealed class WindowsClipboard
{
    private readonly IWindowsClipboardApi _api;
    private readonly nint _window;
    private readonly uint _markerFormat, _excludeFormat, _historyFormat, _cloudFormat;
    private byte[]? _marker;
    private uint _sequence;

    public WindowsClipboard(nint window, IWindowsClipboardApi api)
    {
        if (window == 0) throw new ArgumentException("A native window handle is required.");
        _api = api; _window = window;
        _markerFormat = api.Register("PwdVault.SecretCopy.v1");
        _excludeFormat = api.Register("ExcludeClipboardContentFromMonitorProcessing");
        _historyFormat = api.Register("CanIncludeInClipboardHistory");
        _cloudFormat = api.Register("CanUploadToCloudClipboard");
    }

    public void CopyPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Contains('\0')) throw new ArgumentException("Passwords containing a null character cannot be copied safely.");
        byte[] text = new UnicodeEncoding(false, false, true).GetBytes(password + "\0");
        bool opened = false;
        bool emptied = false;
        try
        {
            if (!_api.Open(_window)) throw new IOException("Clipboard is busy. Try copying again.");
            opened = true;
            if (!_api.Empty()) throw new IOException("Could not empty the clipboard.");
            emptied = true;
            _marker = null;
            // Exclusion flags are installed before text becomes available.
            _api.Put(_excludeFormat, [0, 0, 0, 0]);
            _api.Put(_historyFormat, [0, 0, 0, 0]);
            _api.Put(_cloudFormat, [0, 0, 0, 0]);
            byte[] marker = RandomNumberGenerator.GetBytes(16);
            _api.Put(_markerFormat, marker);
            _api.Put(13, text); // CF_UNICODETEXT
            _marker = marker;
            _sequence = _api.Sequence;
        }
        catch { if (opened && emptied) { _api.Empty(); _marker = null; } throw; }
        finally { CryptographicOperations.ZeroMemory(text); if (opened) _api.Close(); }
    }

    public ClipboardClearResult ClearOwnedPassword()
    {
        if (_marker is null) return ClipboardClearResult.NothingToClear;
        if (!_api.Open(_window)) return ClipboardClearResult.Busy;
        try
        {
            // Holding OpenClipboard prevents a competing copy between the checks and EmptyClipboard.
            bool ours = _api.Owner == _window && _api.Sequence == _sequence &&
                _api.ReadMarker(_markerFormat) is byte[] marker && marker.AsSpan().SequenceEqual(_marker);
            if (ours && !_api.Empty()) return ClipboardClearResult.Busy;
            _marker = null;
            return ours ? ClipboardClearResult.Cleared : ClipboardClearResult.NothingToClear;
        }
        finally { _api.Close(); }
    }
}
