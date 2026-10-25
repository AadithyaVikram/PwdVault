using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using PwdVault.Core;
using PwdVault.Platform.Windows;
using PwdVault.Security;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--benchmark-only" }))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { CheckedUtc = DateTimeOffset.UtcNow, MemoryKiB = 65536,
                Iterations = 3, Parallelism = 1, UnlockTimingsMs = MeasureKdf(), Limitation = "This machine only; not minimum-hardware certification." },
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (!OperatingSystem.IsWindows()) return 2;
        return Run();
    }

    [SupportedOSPlatform("windows")]
    private static int Run()
    {
        var checks = new List<string>();
        nint originalStation = Native.GetProcessWindowStation();
        nint originalDesktop = Native.GetThreadDesktop(Native.GetCurrentThreadId());
        uint userSequence = Native.GetClipboardSequenceNumber();
        nint station = 0, desktop = 0, first = 0, second = 0;
        try
        {
            station = Native.CreateWindowStation("PwdVault-Probe-" + Guid.NewGuid().ToString("N"), 0, 0x37F, 0);
            Require(station != 0, "Create isolated window station");
            Require(Native.SetProcessWindowStation(station), "Assign isolated window station");
            desktop = Native.CreateDesktop("Probe", 0, 0, 0, 0x1FF, 0);
            Require(desktop != 0 && Native.SetThreadDesktop(desktop), "Assign private desktop");
            first = Native.CreateWindowEx(0, "STATIC", "PwdVault test owner", 0x80000000, 0, 0, 16, 16, 0, 0, 0, 0);
            second = Native.CreateWindowEx(0, "STATIC", "PwdVault alternate owner", 0x80000000, 0, 0, 16, 16, 0, 0, 0, 0);
            Require(first != 0 && second != 0, "Create noninteractive clipboard owners");
            var api = new Win32ClipboardApi();
            var clipboard = new WindowsClipboard(first, api);
            clipboard.CopyPassword("synthetic native clipboard probe");
            Require(api.Open(first), "Inspect native clipboard flags");
            try
            {
                foreach (string flag in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                {
                    nint data = Native.GetClipboardData(api.Register(flag));
                    Require(data != 0, "Publish " + flag);
                    nint pointer = Native.GlobalLock(data);
                    Require(pointer != 0, "Read " + flag);
                    try { Require(Marshal.ReadInt32(pointer) == 0, flag + " equals zero"); }
                    finally { Native.GlobalUnlock(data); }
                }
                Require(Native.GetClipboardData(api.Register("ExcludeClipboardContentFromMonitorProcessing")) != 0,
                    "Publish monitor exclusion");
            }
            finally { api.Close(); }
            Require(clipboard.ClearOwnedPassword() == ClipboardClearResult.Cleared, "Clear own native password copy");
            checks.Add("Native ownership tracking, clearing, and history/sync exclusion format publication");

            clipboard.CopyPassword("identical synthetic text");
            Require(api.Open(second), "Copy from alternate owner");
            try
            {
                Require(api.Empty(), "Empty isolated clipboard");
                api.Put(13, Encoding.Unicode.GetBytes("identical synthetic text\0"));
            }
            finally { api.Close(); }
            Require(clipboard.ClearOwnedPassword() == ClipboardClearResult.NothingToClear, "Preserve identical text from a later owner");
            checks.Add("Identical subsequent copy preserved");

            clipboard.CopyPassword("synthetic contention test");
            using var acquired = new ManualResetEvent(false);
            using var release = new ManualResetEvent(false);
            Exception? error = null;
            var holder = new Thread(() =>
            {
                try
                {
                    Require(Native.SetThreadDesktop(desktop), "Assign contention thread desktop");
                    Require(api.Open(second), "Hold clipboard from another thread");
                    try { acquired.Set(); release.WaitOne(TimeSpan.FromSeconds(10)); }
                    finally { api.Close(); }
                }
                catch (Exception ex) { error = ex; acquired.Set(); }
            });
            holder.Start();
            try
            {
                Require(acquired.WaitOne(TimeSpan.FromSeconds(5)), "Start contention test");
                if (error is not null) throw error;
                Require(clipboard.ClearOwnedPassword() == ClipboardClearResult.Busy, "Report native clipboard contention");
            }
            finally { release.Set(); holder.Join(); }
            Require(clipboard.ClearOwnedPassword() == ClipboardClearResult.Cleared, "Retry native clipboard clear");
            checks.Add("Native contention and retry");

            var timings = MeasureKdf();
            if (second != 0) { Native.DestroyWindow(second); second = 0; }
            if (first != 0) { Native.DestroyWindow(first); first = 0; }
            Require(Native.SetProcessWindowStation(originalStation) && Native.SetThreadDesktop(originalDesktop), "Restore original process desktop");
            Require(Native.GetClipboardSequenceNumber() == userSequence, "Leave user clipboard unchanged");
            checks.Add("User clipboard sequence unchanged");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Passed = true, CheckedUtc = DateTimeOffset.UtcNow, Checks = checks,
                Argon2MemoryKiB = 65536, Argon2Iterations = 3, Argon2Parallelism = 1, UnlockTimingsMs = timings,
                Limitation = "Isolated formats verified; actual Windows history/cloud services, session locking, and suspend/resume still need interactive acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Passed = false, CheckedUtc = DateTimeOffset.UtcNow, FailedCheck = ex.Message,
                NativeError = ex is Win32Exception win32 ? win32.NativeErrorCode : 0,
                Limitation = "No fallback uses the interactive clipboard. Run this probe only in an environment permitting an isolated window station."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally
        {
            if (second != 0) Native.DestroyWindow(second);
            if (first != 0) Native.DestroyWindow(first);
            Native.SetProcessWindowStation(originalStation);
            Native.SetThreadDesktop(originalDesktop);
            if (desktop != 0) Native.CloseDesktop(desktop);
            if (station != 0) Native.CloseWindowStation(station);
        }
    }

    private static List<double> MeasureKdf()
    {
        var timings = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            using var vault = VaultCodec.Create("synthetic benchmark passphrase");
            byte[] encrypted = vault.Encrypt();
            long start = Stopwatch.GetTimestamp();
            using var unlocked = VaultCodec.Unlock(encrypted, "synthetic benchmark passphrase");
            timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        return timings;
    }

    private static void Require(bool ok, string operation)
    {
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed.");
    }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern nint GetProcessWindowStation();
        [DllImport("user32.dll")] internal static extern nint GetThreadDesktop(uint thread);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowStation(string name, uint flags, uint access, nint security);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessWindowStation(nint station);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint security);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetThreadDesktop(nint desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(nint desktop);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseWindowStation(nint station);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern nint GetClipboardData(uint format);
        [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalUnlock(nint memory);
    }
}
