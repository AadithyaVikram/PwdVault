using System.Text;
using PwdVault.Core;
using PwdVault.Platform.Windows;
using Xunit;

namespace PwdVault.Tests;

public sealed class ClipboardTests
{
    [Fact]
    public void CopySetsHistoryAndSyncExclusionsBeforeTextAndClearsOnlyOwnedCopy()
    {
        var api = new FakeClipboard();
        var clipboard = new WindowsClipboard(42, api);
        clipboard.CopyPassword("test-password");
        Assert.Equal("ExcludeClipboardContentFromMonitorProcessing", api.WriteOrder[0]);
        Assert.Equal("CanIncludeInClipboardHistory", api.WriteOrder[1]);
        Assert.Equal("CanUploadToCloudClipboard", api.WriteOrder[2]);
        Assert.Equal("text", api.WriteOrder[^1]);
        Assert.Equal(new byte[4], api.Data[api.Formats["CanIncludeInClipboardHistory"]]);
        Assert.Equal(new byte[4], api.Data[api.Formats["CanUploadToCloudClipboard"]]);
        Assert.Equal("test-password\0", Encoding.Unicode.GetString(api.Data[13]));
        Assert.Equal(ClipboardClearResult.Cleared, clipboard.ClearOwnedPassword());
        Assert.Empty(api.Data);
        Assert.Equal(ClipboardClearResult.NothingToClear, clipboard.ClearOwnedPassword());
    }

    [Theory]
    [InlineData("something else")]
    [InlineData("same-password")]
    public void LaterCopiesArePreservedEvenWhenTheirTextMatches(string replacement)
    {
        var api = new FakeClipboard();
        var clipboard = new WindowsClipboard(42, api);
        clipboard.CopyPassword("same-password");
        api.OtherCopy(replacement);
        Assert.Equal(ClipboardClearResult.NothingToClear, clipboard.ClearOwnedPassword());
        Assert.Equal(replacement + "\0", Encoding.Unicode.GetString(api.Data[13]));
    }

    [Fact]
    public void SameOwnerWithChangedSequenceIsPreserved()
    {
        var api = new FakeClipboard();
        var clipboard = new WindowsClipboard(42, api);
        clipboard.CopyPassword("test");
        api.Sequence++;
        Assert.Equal(ClipboardClearResult.NothingToClear, clipboard.ClearOwnedPassword());
        Assert.NotEmpty(api.Data);
    }

    [Fact]
    public void BusyClipboardIsRetriedWithoutLosingOwnershipTracking()
    {
        var api = new FakeClipboard();
        var clipboard = new WindowsClipboard(42, api);
        clipboard.CopyPassword("test");
        api.Busy = true;
        Assert.Equal(ClipboardClearResult.Busy, clipboard.ClearOwnedPassword());
        api.Busy = false;
        Assert.Equal(ClipboardClearResult.Cleared, clipboard.ClearOwnedPassword());
    }

    [Fact]
    public void FailedFlagPublicationDoesNotLeavePasswordTextOnClipboard()
    {
        var api = new FakeClipboard { FailOnWrite = 2 };
        var clipboard = new WindowsClipboard(42, api);
        Assert.Throws<IOException>(() => clipboard.CopyPassword("test"));
        Assert.Empty(api.Data);
        Assert.False(api.IsOpen);
    }

    private sealed class FakeClipboard : IWindowsClipboardApi
    {
        public Dictionary<string, uint> Formats { get; } = [];
        public Dictionary<uint, byte[]> Data { get; } = [];
        public List<string> WriteOrder { get; } = [];
        public bool Busy { get; set; }
        public bool IsOpen { get; private set; }
        public int FailOnWrite { get; init; } = -1;
        public nint Owner { get; private set; }
        public uint Sequence { get; set; }
        private nint _opener;

        public uint Register(string name)
        {
            uint id = (uint)Formats.Count + 100;
            Formats.Add(name, id);
            return id;
        }

        public bool Open(nint window)
        {
            if (Busy || IsOpen) return false;
            IsOpen = true; _opener = window; return true;
        }
        public void Close() { Assert.True(IsOpen); IsOpen = false; }
        public bool Empty()
        {
            Assert.True(IsOpen);
            Owner = _opener; Data.Clear(); Sequence++; return true;
        }
        public void Put(uint format, byte[] bytes)
        {
            Assert.True(IsOpen);
            WriteOrder.Add(format == 13 ? "text" : Formats.Single(p => p.Value == format).Key);
            if (WriteOrder.Count == FailOnWrite) throw new IOException("Simulated clipboard error.");
            Data[format] = (byte[])bytes.Clone(); Sequence++;
        }
        public byte[]? ReadMarker(uint format) { Assert.True(IsOpen); return Data.GetValueOrDefault(format); }
        public void OtherCopy(string text)
        {
            Assert.False(IsOpen);
            Owner = 999; Sequence++; Data.Clear(); Data[13] = Encoding.Unicode.GetBytes(text + "\0");
        }
    }
}
