using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia.Threading;
using GitFlick.Models;
using GitFlick.Services.Interop;

namespace GitFlick.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey.
///
/// Thread affinity is the whole design here. A window's messages are only ever delivered to
/// the thread that created it, and GetMessage only pulls messages for the calling thread.
/// So one dedicated thread creates a message-only window, owns the RegisterHotKey call, and
/// runs the message loop. Callers on the UI thread hand work to it via PostMessage rather
/// than touching user32 directly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32GlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0x4746; // 'GF'

    private const uint WmAppRegister = Win32.WM_APP + 1;
    private const uint WmAppUnregister = Win32.WM_APP + 2;
    private const uint WmAppQuit = Win32.WM_APP + 3;

    private readonly Thread _messageThread;
    private readonly ManualResetEventSlim _windowReady = new(false);
    private readonly Lock _registerGate = new();

    // The delegate must outlive every native call that can invoke it. If this is a local,
    // the GC collects it and the window procedure becomes a dangling pointer.
    private Win32.WndProcDelegate? _wndProc;

    private IntPtr _hwnd;
    private IntPtr _classNamePtr;
    private ushort _classAtom;
    private IntPtr _moduleHandle;

    private HotkeyDefinition? _pendingHotkey;
    private ManualResetEventSlim? _registerCompleted;
    private bool _registerSucceeded;
    private int _registerErrorCode;

    private bool _disposed;

    public Win32GlobalHotkeyService()
    {
        _messageThread = new Thread(RunMessageLoop)
        {
            Name = "GitFlick.Hotkey",
            IsBackground = true,
        };

        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();

        // Nothing may post to the window before it exists.
        _windowReady.Wait();
    }

    public event EventHandler? HotkeyPressed;

    public bool TryRegister(HotkeyDefinition hotkey, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hwnd == IntPtr.Zero)
        {
            error = "Could not create the hotkey message window.";
            return false;
        }

        if (!VirtualKeyMapper.TryToVirtualKey(hotkey.Key, out _))
        {
            error = $"'{hotkey.Key}' cannot be used as a global hotkey.";
            return false;
        }

        lock (_registerGate)
        {
            using var completed = new ManualResetEventSlim(false);

            _pendingHotkey = hotkey;
            _registerCompleted = completed;

            // RegisterHotKey has to happen on the thread that owns the window, so hand it over.
            if (!Win32.PostMessageW(_hwnd, WmAppRegister, IntPtr.Zero, IntPtr.Zero))
            {
                _registerCompleted = null;
                error = "Could not reach the hotkey message loop.";
                return false;
            }

            completed.Wait();
            _registerCompleted = null;

            if (_registerSucceeded)
            {
                error = null;
                return true;
            }

            error = _registerErrorCode == Win32.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"{hotkey.ToDisplayString()} is already in use by another application. GitFlick is running in the tray, but the hotkey is inactive."
                : $"Could not register {hotkey.ToDisplayString()} (Win32 error {_registerErrorCode}).";

            return false;
        }
    }

    public void Unregister()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        Win32.PostMessageW(_hwnd, WmAppUnregister, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            Win32.PostMessageW(_hwnd, WmAppQuit, IntPtr.Zero, IntPtr.Zero);
        }

        // If the loop wedges, don't hold up process exit -- the thread is a background thread.
        _messageThread.Join(TimeSpan.FromSeconds(2));

        if (_classAtom != 0)
        {
            Win32.UnregisterClassW(_classNamePtr, _moduleHandle);
        }

        if (_classNamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_classNamePtr);
            _classNamePtr = IntPtr.Zero;
        }

        _windowReady.Dispose();
    }

    private void RunMessageLoop()
    {
        try
        {
            CreateMessageOnlyWindow();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Hotkey message window could not be created: {ex}");
        }
        finally
        {
            _windowReady.Set();
        }

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // GetMessage returns 0 on WM_QUIT and -1 on error; both must exit the loop.
        while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.DispatchMessageW(ref msg);
        }
    }

    private void CreateMessageOnlyWindow()
    {
        // A unique class name per instance keeps a second registration from colliding.
        var className = $"GitFlick.HotkeyWindow.{Guid.NewGuid():N}";

        _wndProc = WndProc;
        _classNamePtr = Marshal.StringToHGlobalUni(className);
        _moduleHandle = Win32.GetModuleHandleW(IntPtr.Zero);

        var windowClass = new Win32.WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = _moduleHandle,
            LpszClassName = _classNamePtr,
        };

        _classAtom = Win32.RegisterClassExW(ref windowClass);

        if (_classAtom == 0)
        {
            Trace.TraceError($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        _hwnd = Win32.CreateWindowExW(
            dwExStyle: 0,
            lpClassName: _classNamePtr,
            lpWindowName: _classNamePtr,
            dwStyle: 0,
            x: 0, y: 0, nWidth: 0, nHeight: 0,
            hWndParent: Win32.HWND_MESSAGE,
            hMenu: IntPtr.Zero,
            hInstance: _moduleHandle,
            lpParam: IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Trace.TraceError($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>Runs on the hotkey thread, never on the UI thread.</summary>
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY:
                RaiseHotkeyPressed();
                return IntPtr.Zero;

            case WmAppRegister:
                HandleRegister(hWnd);
                return IntPtr.Zero;

            case WmAppUnregister:
                Win32.UnregisterHotKey(hWnd, HotkeyId);
                return IntPtr.Zero;

            case WmAppQuit:
                Win32.UnregisterHotKey(hWnd, HotkeyId);
                Win32.DestroyWindow(hWnd);
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void HandleRegister(IntPtr hWnd)
    {
        var hotkey = _pendingHotkey;

        if (hotkey is null)
        {
            _registerSucceeded = false;
            _registerErrorCode = 0;
            _registerCompleted?.Set();
            return;
        }

        // Re-registering the same id is not allowed, so always clear the previous one first.
        Win32.UnregisterHotKey(hWnd, HotkeyId);

        VirtualKeyMapper.TryToVirtualKey(hotkey.Key, out var virtualKey);
        var modifiers = VirtualKeyMapper.ToModifiers(hotkey.Modifiers) | Win32.MOD_NOREPEAT;

        _registerSucceeded = Win32.RegisterHotKey(hWnd, HotkeyId, modifiers, virtualKey);
        _registerErrorCode = _registerSucceeded ? 0 : Marshal.GetLastWin32Error();

        _registerCompleted?.Set();
    }

    private void RaiseHotkeyPressed()
    {
        var handler = HotkeyPressed;

        if (handler is null)
        {
            return;
        }

        // Windows hands the foreground grant to whoever received the hotkey, but it lapses on
        // the next input event. Send is the highest dispatcher priority: get onto the UI thread
        // now, and do not await anything between here and SetForegroundWindow.
        Dispatcher.UIThread.Post(() => handler(this, EventArgs.Empty), DispatcherPriority.Send);
    }
}
