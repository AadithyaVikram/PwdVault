using System.Text.Json.Serialization;

namespace PwdVault.Core;

public sealed record VaultEntry
{
    [JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();
    [JsonRequired]
    public string Title { get; init; } = "";
    [JsonRequired]
    public string Username { get; init; } = "";
    [JsonRequired]
    public string Password { get; init; } = "";
    [JsonRequired]
    public string Url { get; init; } = "";
    [JsonRequired]
    public string Notes { get; init; } = "";

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Title))
            throw new InvalidDataException("Each entry needs a title and a valid identifier.");
        if (Title.Length > 256 || Username is null || Username.Length > 4096 ||
            Password is null || Password.Length > 4096 || Url is null || Url.Length > 4096 ||
            Notes is null || Notes.Length > 65536)
            throw new InvalidDataException("An entry exceeds the supported field lengths.");
    }
}

public enum ClipboardClearResult { NothingToClear, Cleared, Busy }

public interface IPlatformSecurity : IDisposable
{
    event Action? LockRequested;
    void CopyPassword(string password);
    ClipboardClearResult ClearOwnedPassword();
}
