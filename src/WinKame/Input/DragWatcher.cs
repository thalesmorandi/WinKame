// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Runtime.InteropServices;
using System.Windows.Threading;
using WinKame.Interop;
using WinKame.Wheel;

namespace WinKame.Input;

/// <summary>
/// Watches the mouse and keyboard globally. When a key is tapped while the left button is held
/// and the mouse has moved (a drag in progress), it reports which wheel to open.
/// Hooks live on their own thread so a busy UI thread can never stall system input.
/// </summary>
internal sealed class DragWatcher : IDisposable
{
    private readonly Dispatcher _ui;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _keyHook;
    private Native.HookProc? _mouseProc, _keyProc; // keep delegates alive

    private volatile bool _leftDown;
    private volatile bool _dragActive;
    private Native.POINT _downPt;
    private long _dragStart;

    private readonly Dictionary<WheelKind, (long Time, bool Interrupted)> _pressed = new();
    private Dictionary<uint, WheelKind> _keyMap = new();

    public int ThresholdPx { get; set; } = 10;
    public int TapMaxMs { get; set; } = 450;
    public bool Paused { get; set; }

    /// <summary>Raised on the UI thread with the wheel to open and the cursor position (physical pixels).</summary>
    public event Action<WheelKind, Native.POINT>? Tapped;

    /// <summary>Raised on the UI thread when the left button is released after a drag.</summary>
    public event Action? DragEnded;

    public bool IsDragging => _dragActive && _leftDown;

    public DragWatcher(Dispatcher ui) => _ui = ui;

    public void SetKeys(string convert, string tools, string actions, string custom)
    {
        var map = new Dictionary<uint, WheelKind>();
        void Add(string key, WheelKind kind)
        {
            foreach (var vk in VirtualKeys(key)) map.TryAdd(vk, kind);
        }
        Add(convert, WheelKind.Convert);
        Add(tools, WheelKind.Tools);
        Add(actions, WheelKind.Actions);
        Add(custom, WheelKind.Custom);
        _keyMap = map;
    }

    public static IReadOnlyList<string> KeyNames { get; } = ["Shift", "Ctrl", "Alt", "Space", "Tab", "None"];

    public static string KeyLabel(string key) => key switch
    {
        "Shift" => "⇧ Shift",
        "Ctrl" => "Ctrl",
        "Alt" => "Alt",
        "Space" => T("Espaço"),
        "Tab" => "Tab",
        _ => "—",
    };

    private static uint[] VirtualKeys(string key) => key switch
    {
        "Shift" => [0x10, 0xA0, 0xA1],
        "Ctrl" => [0x11, 0xA2, 0xA3],
        "Alt" => [0x12, 0xA4, 0xA5],
        "Space" => [0x20],
        "Tab" => [0x09],
        _ => [],
    };

    private static bool IsAlt(uint vk) => vk is 0x12 or 0xA4 or 0xA5;

    public void Start()
    {
        if (_thread is not null) return;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _mouseProc = MouseProc;
            _keyProc = KeyProc;
            var mod = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
            _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
            ready.Set();
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(in msg);
                Native.DispatchMessage(in msg);
            }
            if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
            if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
            _mouseHook = _keyHook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "WinKame.Hooks",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(3000);
    }

    public bool HooksInstalled => _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero;

    public void Dispose()
    {
        if (_thread is null) return;
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                    _leftDown = true;
                    _dragActive = false;
                    _downPt = data.pt;
                    break;
                case Native.WM_MOUSEMOVE:
                    if (_leftDown && !_dragActive)
                    {
                        var dx = data.pt.X - _downPt.X;
                        var dy = data.pt.Y - _downPt.Y;
                        if (dx * dx + dy * dy >= ThresholdPx * ThresholdPx)
                        {
                            _dragActive = true;
                            _dragStart = Environment.TickCount64;
                        }
                    }
                    break;
                case Native.WM_LBUTTONUP:
                    var wasDragging = _dragActive;
                    _leftDown = false;
                    _dragActive = false;
                    lock (_pressed) _pressed.Clear();
                    if (wasDragging) _ui.BeginInvoke(() => DragEnded?.Invoke());
                    break;
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !Paused)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (data.dwExtraInfo != Native.OurExtraInfo)
            {
                var msg = (int)wParam;
                var down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                var up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                var now = Environment.TickCount64;
                var mapped = _keyMap.TryGetValue(data.vkCode, out var kind);

                lock (_pressed)
                {
                    if (down)
                    {
                        if (!mapped)
                        {
                            // Another key pressed in between: it's a shortcut, not a tap.
                            foreach (var k in _pressed.Keys.ToList()) _pressed[k] = (_pressed[k].Time, true);
                        }
                        else if (!_pressed.ContainsKey(kind))
                        {
                            _pressed[kind] = (now, false);
                            if (IsAlt(data.vkCode) && IsDragging) Native.SendDummyKey();
                        }
                    }
                    else if (up && mapped && _pressed.Remove(kind, out var st))
                    {
                        var isTap = !st.Interrupted
                            && now - st.Time <= TapMaxMs
                            && IsDragging
                            && st.Time >= _dragStart; // keys held before the drag (e.g. Ctrl+drag to copy) don't count
                        if (isTap)
                        {
                            var pt = Native.CursorPos();
                            _ui.BeginInvoke(() => Tapped?.Invoke(kind, pt));
                        }
                    }
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
