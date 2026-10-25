namespace PwdVault.Core;

// Monotonic time avoids changes to the Windows wall clock extending secret lifetimes.
public sealed class SecurityTimeouts
{
    private readonly TimeProvider _clock;
    private long _lastActivity;
    private long? _copied;
    public SecurityTimeouts(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        RecordActivity();
    }
    public void RecordActivity() => _lastActivity = _clock.GetTimestamp();
    public void RecordCopy() => _copied = _clock.GetTimestamp();
    public void ForgetCopy() => _copied = null;
    public void RetryClear() => _copied = _clock.GetTimestamp() - checked(_clock.TimestampFrequency * 20);
    public bool ShouldLock => _clock.GetElapsedTime(_lastActivity) >= TimeSpan.FromMinutes(5);
    public bool ShouldClearCopy => _copied is long start && _clock.GetElapsedTime(start) >= TimeSpan.FromSeconds(20);
}
