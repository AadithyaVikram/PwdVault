using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PwdVault.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class Win32ClipboardApi : IWindowsClipboardApi
{
    public uint Register(string name)
    {
        uint format = Native.RegisterClipboardFormat(name);
        return format != 0 ? format : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public bool Open(nint window) => Native.OpenClipboard(window);
    public void Close() => Native.CloseClipboard();
    public bool Empty() => Native.EmptyClipboard();
    public nint Owner => Native.GetClipboardOwner();
    public uint Sequence => Native.GetClipboardSequenceNumber();

    public byte[]? ReadMarker(uint format)
    {
        nint data = Native.GetClipboardData(format);
        if (data == 0 || Native.GlobalSize(data) < 16) return null;
        nint address = Native.GlobalLock(data);
        if (address == 0) return null;
        try
        {
            byte[] bytes = new byte[16];
            Marshal.Copy(address, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Native.GlobalUnlock(data); }
    }

    public void Put(uint format, byte[] bytes)
    {
        nint memory = Native.GlobalAlloc(0x0042, (nuint)bytes.Length); // MOVEABLE | ZEROINIT
        if (memory == 0) throw new OutOfMemoryException();
        bool transferred = false;
        try
        {
            nint address = Native.GlobalLock(memory);
            if (address == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, address, bytes.Length); }
            finally { Native.GlobalUnlock(memory); }
            if (Native.SetClipboardData(format, memory) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            transferred = true;
        }
        finally
        {
            if (!transferred)
            {
                nint address = Native.GlobalLock(memory);
                if (address != 0)
                {
                    for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(address, i, 0);
                    Native.GlobalUnlock(memory);
                }
                Native.GlobalFree(memory);
            }
        }
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyClipboard();
        [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetClipboardData(uint format, nint memory);
        [DllImport("user32.dll")] internal static extern nint GetClipboardData(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint RegisterClipboardFormat(string format);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GlobalAlloc(uint flags, nuint size);
        [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(nint memory);
        [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint memory);
        [DllImport("kernel32.dll")] internal static extern nuint GlobalSize(nint memory);
    }
}
