using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PwdVault.Core;
using PwdVault.Security;
using PwdVault.Storage;

namespace PwdVault.Desktop;

public sealed record EntrySummary(Guid Id, string Title, string Username);

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private VaultFile? _vault;
    private bool _locked = true;
    private bool _busy;
    private int _epoch;
    private string _status = "Create a vault or choose an existing one.";
    private string _path = "";
    private string _search = "";
    private Guid _editingId = Guid.NewGuid();
    private string _title = "", _username = "", _password = "", _url = "", _notes = "";
    private bool _reveal;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<EntrySummary> Entries { get; } = [];
    public bool IsUnlocked => !_locked && _vault is not null;
    public bool IsLocked => !IsUnlocked;
    public bool IsBusy { get => _busy; private set { _busy = value; Notify(); Notify(nameof(CanInteract)); Notify(nameof(CanEdit)); } }
    public bool CanInteract => !IsBusy;
    public bool CanEdit => IsUnlocked && !IsBusy;
    public bool IsExistingEntry => IsUnlocked && _vault!.Session.Entries.Any(e => e.Id == _editingId);
    public string VaultPath { get => _path; set => Set(ref _path, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }
    public string EntryTitle { get => _title; set => Set(ref _title, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public bool Reveal { get => _reveal; set { if (Set(ref _reveal, value)) Notify(nameof(PasswordMask)); } }
    public char PasswordMask => Reveal ? '\0' : '\u25cf';

    public async Task OpenAsync(string passphrase, bool create)
    {
        if (IsBusy || IsUnlocked) return;
        int epoch = _epoch;
        string path = VaultPath;
        IsBusy = true;
        Status = create ? "Creating your encrypted vault..." : "Unlocking...";
        try
        {
            var opened = await Task.Run(() => create ? VaultFile.Create(path, passphrase) : VaultFile.Open(path, passphrase));
            if (epoch != _epoch) { opened.Dispose(); return; }
            _vault = opened;
            _locked = false;
            NotifyState();
            NewEntry();
            Refresh();
            Status = "Vault unlocked. Changes are saved when you choose Save entry.";
        }
        catch (Exception ex) when (IsExpected(ex)) { if (epoch == _epoch) Status = Friendly(ex); }
        finally { IsBusy = false; }
    }

    public void NewEntry()
    {
        _editingId = Guid.NewGuid();
        EntryTitle = Username = Password = Url = Notes = "";
        Reveal = false;
        Notify(nameof(IsExistingEntry));
    }

    public void Select(EntrySummary? summary)
    {
        if (!CanEdit || summary is null) return;
        var entry = _vault!.Session.Entries.FirstOrDefault(e => e.Id == summary.Id);
        if (entry is null) return;
        _editingId = entry.Id;
        EntryTitle = entry.Title; Username = entry.Username; Password = entry.Password;
        Url = entry.Url; Notes = entry.Notes; Reveal = false;
        Notify(nameof(IsExistingEntry));
    }

    public Task SaveAsync()
    {
        if (!CanEdit) return Task.CompletedTask;
        var entry = new VaultEntry { Id = _editingId, Title = EntryTitle.Trim(), Username = Username,
            Password = Password, Url = Url, Notes = Notes };
        return ExecuteAsync(() => _vault!.SaveEntry(entry), "Entry saved.");
    }

    public async Task DeleteAsync()
    {
        if (!CanEdit || !IsExistingEntry) return;
        Guid id = _editingId;
        if (await ExecuteAsync(() => _vault!.DeleteEntry(id), "Entry deleted.") && IsUnlocked) NewEntry();
    }

    public Task ChangePassphraseAsync(string current, string replacement) =>
        ExecuteAsync(() => _vault!.ChangePassphrase(current, replacement),
            "Master passphrase changed. Older backups still use their original passphrase.");

    public Task BackupAsync(string path) => ExecuteAsync(() => _vault!.ExportBackup(path), "Encrypted backup saved.");

    public async Task RestoreAsync(string path, string passphrase)
    {
        if (await ExecuteAsync(() => _vault!.Restore(path, passphrase),
            "Backup restored. Use the backup's master passphrase to unlock this vault.") && IsUnlocked) NewEntry();
    }

    private async Task<bool> ExecuteAsync(Action action, string success)
    {
        if (!CanEdit) return false;
        int epoch = _epoch;
        IsBusy = true;
        bool done = false;
        try
        {
            await Task.Run(action);
            done = true;
            if (epoch == _epoch) { Refresh(); Status = success; Notify(nameof(IsExistingEntry)); }
        }
        catch (Exception ex) when (IsExpected(ex)) { if (epoch == _epoch) Status = Friendly(ex); }
        finally
        {
            if (_locked) { _vault?.Dispose(); _vault = null; }
            IsBusy = false;
        }
        return done;
    }

    public void Lock()
    {
        _epoch++;
        _locked = true;
        Entries.Clear();
        Search = "";
        NewEntry();
        if (!IsBusy) { _vault?.Dispose(); _vault = null; }
        NotifyState();
        Status = "Vault locked.";
    }

    private void Refresh()
    {
        Entries.Clear();
        if (!IsUnlocked) return;
        foreach (var e in _vault!.Session.Entries
            .Where(e => e.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                e.Username.Contains(Search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase))
            Entries.Add(new EntrySummary(e.Id, e.Title, e.Username));
    }

    private void NotifyState()
    {
        Notify(nameof(IsUnlocked)); Notify(nameof(IsLocked)); Notify(nameof(CanEdit)); Notify(nameof(IsExistingEntry));
    }

    private static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or System.Security.Cryptography.CryptographicException or VaultUnlockException or System.Text.Json.JsonException;

    private static string Friendly(Exception ex) => ex switch
    {
        VaultUnlockException => ex.Message,
        System.Text.Json.JsonException => "The vault contains invalid data.",
        UnauthorizedAccessException => "Access denied. Choose a location you can read and write.",
        IOException when ex is not InvalidDataException && ex.HResult == unchecked((int)0x80070020) =>
            "This vault or its backup is already open in another application.",
        _ => ex.Message
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); return true;
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() => Lock();
}
