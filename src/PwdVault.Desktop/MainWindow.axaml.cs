using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PwdVault.Core;
using PwdVault.Platform.Windows;
using PwdVault.Security;

namespace PwdVault.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IPlatformSecurity? _platform;
    private readonly SecurityTimeouts _timeouts = new();
    private bool _create;
    private bool _closeWhenIdle;
    private Window? _dialog;
    private readonly FilePickerFileType _vaultType = new("PwdVault files") { Patterns = ["*.pwdvault"] };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        AddHandler(PointerPressedEvent, Activity, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, Activity, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, Activity, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _platform = new WindowsSecurity(TryGetPlatformHandle()!.Handle);
                    _platform.LockRequested += OnPlatformLock;
                }
                catch { _vm.Status = "Windows security integration could not start. Restart the app before using a vault."; }
            }
            else _vm.Status = "This first release supports Windows only.";
        };
        Closing += (_, e) =>
        {
            LockVault();
            if (_vm.IsBusy) { e.Cancel = true; _closeWhenIdle = true; }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _platform?.Dispose();
            _vm.Dispose();
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Activity(object? sender, RoutedEventArgs e) => _timeouts.RecordActivity();
    private void OnPlatformLock() => Dispatcher.UIThread.Post(LockVault, DispatcherPriority.Send);

    private void Tick()
    {
        if (_timeouts.ShouldLock) LockVault();
        if (_timeouts.ShouldClearCopy)
            ClearClipboard();
        if (_closeWhenIdle && !_vm.IsBusy) Close();
    }

    private void LockVault()
    {
        _dialog?.Close();
        MasterInput.Text = ConfirmInput.Text = "";
        _vm.Lock();
        ClearClipboard();
        _timeouts.RecordActivity();
    }

    private void ClearClipboard()
    {
        if (_platform is null) return;
        var result = _platform.ClearOwnedPassword();
        if (result == ClipboardClearResult.Busy)
        {
            _timeouts.RetryClear();
            _vm.Status = "Vault locked or copy expired, but the clipboard is busy. Clearing will be retried while the app is open.";
        }
        else
        {
            _timeouts.ForgetCopy();
            if (result == ClipboardClearResult.Cleared) _vm.Status = _vm.IsUnlocked ? "Copied password cleared." : "Vault locked. Copied password cleared.";
        }
    }

    private async void ChooseClicked(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpen("Choose your vault");
        if (path is null) return;
        _create = false; ConfirmationPanel.IsVisible = false; UnlockButton.Content = "Unlock vault";
        MasterInput.Text = ConfirmInput.Text = "";
        _vm.VaultPath = path;
    }

    private async void NewLocationClicked(object? sender, RoutedEventArgs e)
    {
        var path = await PickSave("Create a vault", "My vault.pwdvault");
        if (path is null) return;
        _create = true; ConfirmationPanel.IsVisible = true; UnlockButton.Content = "Create vault";
        MasterInput.Text = ConfirmInput.Text = "";
        _vm.VaultPath = path;
    }

    private async void UnlockClicked(object? sender, RoutedEventArgs e)
    {
        if (_platform is null) { _vm.Status = "Windows security integration is required. Restart on Windows."; return; }
        if (string.IsNullOrWhiteSpace(_vm.VaultPath)) { _vm.Status = "Choose a vault file location first."; return; }
        string passphrase = MasterInput.Text ?? "";
        if (_create && passphrase != (ConfirmInput.Text ?? "")) { _vm.Status = "The passphrases do not match."; return; }
        MasterInput.Text = ConfirmInput.Text = "";
        await _vm.OpenAsync(passphrase, _create);
        _timeouts.RecordActivity();
        if (_vm.IsUnlocked) { _create = false; ConfirmationPanel.IsVisible = false; UnlockButton.Content = "Unlock vault"; }
    }

    private void LockClicked(object? sender, RoutedEventArgs e) => LockVault();
    private void NewEntryClicked(object? sender, RoutedEventArgs e) { EntryList.SelectedItem = null; _vm.NewEntry(); }
    private void EntrySelected(object? sender, SelectionChangedEventArgs e) => _vm.Select(EntryList.SelectedItem as EntrySummary);
    private async void SaveClicked(object? sender, RoutedEventArgs e) => await _vm.SaveAsync();

    private async void DeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (await Confirm("Delete this entry?", "This removes the entry from the current vault. Older encrypted backups may still contain it.", "Delete entry"))
            await _vm.DeleteAsync();
    }

    private void CopyClicked(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanEdit || _platform is null) return;
        try
        {
            _platform.CopyPassword(_vm.Password);
            _timeouts.RecordCopy();
            _vm.Status = "Password copied. It will clear in 20 seconds if the clipboard stays unchanged.";
        }
        catch (ArgumentException ex) { _vm.Status = ex.Message; }
        catch { _vm.Status = "Could not copy the password. The clipboard may be busy; try again."; }
    }

    private async void GenerateClicked(object? sender, RoutedEventArgs e)
    {
        var length = new NumericUpDown { Minimum = 8, Maximum = 256, Value = 24, Increment = 1 };
        var upper = new CheckBox { Content = "Uppercase letters", IsChecked = true };
        var lower = new CheckBox { Content = "Lowercase letters", IsChecked = true };
        var digits = new CheckBox { Content = "Numbers", IsChecked = true };
        var symbols = new CheckBox { Content = "Symbols", IsChecked = true };
        if (await ShowDialogPanel("Generate password", [new TextBlock { Text = "Password length" }, length, upper, lower, digits, symbols], "Generate"))
        {
            try { _vm.Password = PasswordGenerator.Generate((int)(length.Value ?? 24), upper.IsChecked == true, lower.IsChecked == true, digits.IsChecked == true, symbols.IsChecked == true); }
            catch (ArgumentException ex) { _vm.Status = ex.Message; }
        }
    }

    private async void BackupClicked(object? sender, RoutedEventArgs e)
    {
        string? path = await PickSave("Save encrypted backup", "Vault backup.pwdvault");
        if (path is not null) await _vm.BackupAsync(path);
    }

    private async void RestoreClicked(object? sender, RoutedEventArgs e)
    {
        string? path = await PickOpen("Choose encrypted backup");
        if (path is null || !_vm.CanEdit) return;
        var passphrase = PasswordBox("Backup's master passphrase");
        try
        {
            if (await ShowDialogPanel("Restore this backup?", [
                new TextBlock { Text = "This replaces the current entries. A separate encrypted copy of the current vault will be kept beside it.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                passphrase], "Restore backup"))
                await _vm.RestoreAsync(path, passphrase.Text ?? "");
        }
        finally { passphrase.Text = ""; }
    }

    private async void ChangePassphraseClicked(object? sender, RoutedEventArgs e)
    {
        var current = PasswordBox("Current master passphrase");
        var replacement = PasswordBox("New master passphrase");
        var confirmation = PasswordBox("Confirm new passphrase");
        try
        {
            if (await ShowDialogPanel("Change master passphrase", [
                new TextBlock { Text = "Use at least 12 characters. Older backups retain their original passphrase.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                current, replacement, confirmation], "Change passphrase"))
            {
                if (replacement.Text != confirmation.Text) { _vm.Status = "The new passphrases do not match."; return; }
                await _vm.ChangePassphraseAsync(current.Text ?? "", replacement.Text ?? "");
            }
        }
        finally { current.Text = replacement.Text = confirmation.Text = ""; }
    }

    private static TextBox PasswordBox(string watermark) => new() { PasswordChar = '\u25cf', MaxLength = 1024, PlaceholderText = watermark };

    private async Task<string?> PickOpen(string title)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = title, AllowMultiple = false, FileTypeFilter = [_vaultType, FilePickerFileTypes.All] });
            return files.Count == 1 ? files[0].TryGetLocalPath() : null;
        }
        catch { _vm.Status = "The file picker could not open. Try again."; return null; }
    }

    private async Task<string?> PickSave(string title, string suggestedName)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = title, SuggestedFileName = suggestedName, DefaultExtension = "pwdvault", FileTypeChoices = [_vaultType], ShowOverwritePrompt = true });
            return file?.TryGetLocalPath();
        }
        catch { _vm.Status = "The file picker could not open. Try again."; return null; }
    }

    private Task<bool> Confirm(string title, string message, string action) =>
        ShowDialogPanel(title, [new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }], action);

    private async Task<bool> ShowDialogPanel(string title, IEnumerable<Control> fields, string action)
    {
        if (!_vm.CanEdit) return false;
        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        foreach (var field in fields) panel.Children.Add(field);
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        var ok = new Button { Content = action };
        var cancel = new Button { Content = "Cancel" };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var dialog = new Window { Title = title, Width = 450, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.AddHandler(KeyDownEvent, Activity, RoutingStrategies.Tunnel);
        dialog.AddHandler(PointerPressedEvent, Activity, RoutingStrategies.Tunnel);
        _dialog = dialog;
        try { return await dialog.ShowDialog<bool>(this) && _vm.CanEdit; }
        finally { _dialog = null; }
    }
}
