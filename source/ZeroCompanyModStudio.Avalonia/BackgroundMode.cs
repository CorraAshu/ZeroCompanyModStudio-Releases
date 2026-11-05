#if DEV
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Win32;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// DEVELOPER-ONLY: run the Studio so that no window it opens can take the author's focus or appear
/// over his work. Switched on by the ENVIRONMENT VARIABLE ZCMS_BACKGROUND=1 - an environment
/// variable rather than a flag, so every window, every launch path and every child process gets it
/// without each flag parser having to know.
///
/// WHY: the Studio is driven and photographed headless (UI Automation to press things, PrintWindow
/// to photograph them), and that used to mean every test launch popped a window over whatever the
/// author was doing at the time. In this mode every <see cref="Window"/> in the application:
///   - opens WITHOUT activation (it never takes the foreground or the keyboard focus),
///   - is placed OFF-SCREEN, far outside every monitor, BEFORE it is first shown (it never flashes),
///   - is kept out of the taskbar,
///   - and is never maximised (a maximised window would land on the nearest monitor).
/// PrintWindow still renders such a window; that was measured, not assumed (see the task report).
///
/// HOW IT REACHES EVERY WINDOW WITHOUT TOUCHING A CALL SITE. XAML sets WindowStartupLocation and
/// the like on the instance, which beats a property default, and there are some fifty places that
/// call Show or ShowDialog. So instead an application-wide Style selects every Window and sets an
/// attached property; its change handler runs when the window's styles are applied - after the XAML
/// has been loaded, and before Show computes the startup position and creates the native window.
/// What the handler sets therefore wins, whichever code path made the window.
///
/// Compiled out of the release (#if DEV) like every other developer flag; stagecheck.ps1 carries the
/// variable's name as a needle to prove it.
/// </summary>
internal static class BackgroundMode
{
    public const string Variable = "ZCMS_BACKGROUND";

    public static bool On => Environment.GetEnvironmentVariable(Variable) == "1";

    /// <summary>Where "off-screen" is: far outside any monitor arrangement a person could have.</summary>
    private static readonly PixelPoint Away = new(-20000, -20000);

    public static readonly AttachedProperty<bool> QuietProperty =
        AvaloniaProperty.RegisterAttached<Window, Window, bool>("Quiet", defaultValue: false);

    public static void Install(Application app)
    {
        VetoActivation();

        QuietProperty.Changed.AddClassHandler<Window>((w, e) =>
        {
            if (e.NewValue is not true) return;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.WindowState = WindowState.Normal;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = Away;

            // BELT AND BRACES, because setting Position before the first Show was measured NOT to
            // hold: the window opened at (0,0), on a monitor. So the operating system is told too:
            // every position change the window is about to receive is rewritten to the far-away
            // point, whoever asked for it (Show, CenterScreen, a size change that moves it), and a
            // click can never activate it.
            Win32Properties.AddWndProcHookCallback(w, Hook);

        });

        app.Styles.Add(new Style(x => x.OfType<Window>())
        {
            Setters = { new Setter(QuietProperty, true) },
        });
    }

    private static IntPtr Hook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING:
            {
                // The extended style is set HERE, from the window's own first position message,
                // because the handle is not reliably there when the style is applied. NOACTIVATE: a
                // click cannot activate it. TOOLWINDOW: no taskbar button, no alt-tab entry.
                var ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
                if ((ex & (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) != (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW))
                    SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                pos.x = Away.X;
                pos.y = Away.Y;
                pos.flags = (pos.flags & ~SWP_NOMOVE) | SWP_NOACTIVATE;
                Marshal.StructureToPtr(pos, lParam, false);
                break;
            }
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    // NOTHING OF THIS PROCESS MAY TAKE THE FOCUS (the 2026-11-03 incident). The style bits and the two messages above keep a
    // window from being activated by a CLICK or by being SHOWN, but not by a program: when a modal dialog closes, Avalonia
    // calls owner.Activate(), which is SetForegroundWindow(owner), and that ignores WS_EX_NOACTIVATE. Measured on a hidden
    // desktop (task 7 review): after the dialog closed, by UI Automation and by a plain WM_CLOSE alike, the owner was the
    // active and focus window of its input queue within 150 ms; on the real desktop that is the author's keyboard.
    // So the process vetoes it at the root: a thread-local WH_CBT hook on the UI thread, which every activation and every
    // focus change of a window of that thread passes through BEFORE it happens, and which cancels it by returning nonzero.
    // With it the same replays read active=[0] focus=[0] at every sample and the dialog still closes (it is the owner's
    // activation that is refused, not the dialog's closing). Thread-local and on the UI thread: it covers every window of the
    // application (Avalonia runs them all on that thread), needs no DLL, and is gone with the process.
    private static HookProc? _cbtProc;      // kept in a field: the delegate must outlive the hook or the runtime frees it under Windows
    private static IntPtr _cbtHook;

    private static void VetoActivation()
    {
        if (_cbtHook != IntPtr.Zero) return;
        _cbtProc = CbtVeto;
        _cbtHook = SetWindowsHookEx(WH_CBT, _cbtProc, IntPtr.Zero, GetCurrentThreadId());
        // A failure here must not be silent: a mode whose whole job is to keep the author's focus must say that it could not.
        if (_cbtHook == IntPtr.Zero)
            Console.Error.WriteLine("BackgroundMode: the focus veto could not be installed (error " + Marshal.GetLastWin32Error() + "); windows may take the focus.");
    }

    private static IntPtr CbtVeto(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HCBT_ACTIVATE || code == HCBT_SETFOCUS) return new IntPtr(1);   // nonzero = do not do it
        return CallNextHookEx(_cbtHook, code, wParam, lParam);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private const int WH_CBT = 5;
    private const int HCBT_ACTIVATE = 5;
    private const int HCBT_SETFOCUS = 9;

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
#endif
