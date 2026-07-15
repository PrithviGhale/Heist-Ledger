using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GTAEarningsTracker.Services;

[Flags]
public enum GlobalHotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x484C;
    private const int WmHotkey = 0x0312;

    private readonly IntPtr _windowHandle;
    private readonly HwndSource _source;
    private bool _isRegistered;
    private bool _disposed;
    private GlobalHotkeyModifiers _registeredModifiers;
    private uint _registeredVirtualKey;

    public GlobalHotkeyService(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A valid WPF window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("The WPF window source is unavailable.");
        _source.AddHook(WindowMessageHook);
    }

    public event EventHandler? HotkeyPressed;

    public bool IsRegistered => _isRegistered;

    public bool TryRegister(
        GlobalHotkeyModifiers modifiers,
        uint virtualKey,
        out string? errorMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isRegistered &&
            _registeredModifiers == modifiers &&
            _registeredVirtualKey == virtualKey)
        {
            errorMessage = null;
            return true;
        }

        var hadPreviousRegistration = _isRegistered;
        var previousModifiers = _registeredModifiers;
        var previousVirtualKey = _registeredVirtualKey;
        UnregisterCurrent();

        if (RegisterHotKey(
                _windowHandle,
                HotkeyId,
                (uint)(modifiers | GlobalHotkeyModifiers.NoRepeat),
                virtualKey))
        {
            _registeredModifiers = modifiers;
            _registeredVirtualKey = virtualKey;
            _isRegistered = true;
            errorMessage = null;
            return true;
        }

        if (hadPreviousRegistration &&
            RegisterHotKey(
                _windowHandle,
                HotkeyId,
                (uint)(previousModifiers | GlobalHotkeyModifiers.NoRepeat),
                previousVirtualKey))
        {
            _registeredModifiers = previousModifiers;
            _registeredVirtualKey = previousVirtualKey;
            _isRegistered = true;
        }

        errorMessage = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    public void Disable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnregisterCurrent();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterCurrent();
        _source.RemoveHook(WindowMessageHook);
        _disposed = true;
    }

    private IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private void UnregisterCurrent()
    {
        if (!_isRegistered)
        {
            return;
        }

        _ = UnregisterHotKey(_windowHandle, HotkeyId);
        _isRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr windowHandle,
        int hotkeyId,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int hotkeyId);
}
