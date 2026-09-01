using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace ViennaBar.Spike.AppBar;

// S1 — AppBar con auto-hide por borde izquierdo (sliver 2px reservado via
// ABM_SETPOS). Event-driven: cero timers periódicos, solo mensajes.
// Doble clic sobre la barra = cerrar el spike.
internal static unsafe class Program
{
    private const string WndClass = "ViennaBarSpike1";
    private const int FullWidthPx = 260;
    private const int SliverPx = 2;
    private const nuint TimerReveal = 1;
    private const nuint TimerHide = 2;
    private const uint RevealDelayMs = 80;
    private const uint HideDelayMs = 350;

    private static HWND _hwnd;
    private static bool _hidden = true;

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wparam, LPARAM lparam)
    {
        switch (msg)
        {
            case WM_PAINT:
            {
                var ps = default(PAINTSTRUCT);
                var hdc = BeginPaint(hwnd, out ps);
                var brush = CreateSolidBrush(new COLORREF(0x00E8E5B2)); // celeste Vienna
                _ = FillRect(hdc, &ps.rcPaint, brush);
                _ = DeleteObject((HGDIOBJ)brush.Value);
                _ = EndPaint(hwnd, in ps);
                return default;
            }

            case WM_MOUSEMOVE:
                if (_hidden)
                {
                    _ = SetTimer(hwnd, TimerReveal, RevealDelayMs, null);
                }
                else
                {
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)sizeof(TRACKMOUSEEVENT),
                        dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
                        hwndTrack = hwnd,
                    };
                    _ = TrackMouseEvent(ref tme);
                }
                return default;

            case WM_MOUSELEAVE:
                if (!_hidden)
                {
                    _ = SetTimer(hwnd, TimerHide, HideDelayMs, null);
                }
                return default;

            case WM_TIMER:
                switch ((nuint)wparam.Value)
                {
                    case TimerReveal:
                        _ = KillTimer(hwnd, TimerReveal);
                        _ = GetCursorPos(out var pt);
                        if (pt.X <= SliverPx)
                        {
                            SetPos(FullWidthPx, hidden: false);
                        }
                        break;

                    case TimerHide:
                        _ = KillTimer(hwnd, TimerHide);
                        if (!_hidden)
                        {
                            SetPos(SliverPx, hidden: true);
                        }
                        break;
                }
                return default;

            case WM_DPICHANGED:
                SetPos(_hidden ? SliverPx : FullWidthPx, _hidden);
                return default;

            case WM_LBUTTONDBLCLK:
                _ = DestroyWindow(hwnd);
                return default;

            case WM_APP:
                return default; // notificaciones ABN_* (fullscreen, etc.) — F1

            case WM_DESTROY:
                UnregisterAppBar();
                PostQuitMessage(0);
                return default;
        }
        return DefWindowProc(hwnd, msg, wparam, lparam);
    }

    [STAThread]
    private static unsafe int Main()
    {
        var hinst = (HINSTANCE)GetModuleHandle(default(PCWSTR)).Value;

        fixed (char* wndClass = WndClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (WNDPROC)WndProc,
                hInstance = hinst,
                hCursor = LoadCursor(default, IDC_ARROW),
                lpszClassName = wndClass,
                style = WNDCLASS_STYLES.CS_DBLCLKS,
            };
            _ = RegisterClassEx(in wc);

            _hwnd = CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                wndClass, wndClass,
                WINDOW_STYLE.WS_POPUP,
                -FullWidthPx, 0, FullWidthPx, 500,
                default, default, hinst, null);
        }
        if (_hwnd.IsNull) return 1;

        RegisterAppBar();
        SetPos(SliverPx, hidden: true);   // arranca pegado al borde (auto-hide)
        _ = ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        while (GetMessage(out var msg, default, 0, 0))
        {
            TranslateMessage(in msg);
            _ = DispatchMessage(in msg);
        }
        return 0;
    }

    private static void RegisterAppBar()
    {
        var abd = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = WM_APP,
        };
        _ = SHAppBarMessage(ABM_NEW, ref abd);
    }

    private static void UnregisterAppBar()
    {
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        _ = SHAppBarMessage(ABM_REMOVE, ref abd);
    }

    // Reserva la banda left-edge del ancho dado y reposiciona la ventana.
    private static void SetPos(int widthPx, bool hidden)
    {
        var hmon = MonitorFromWindow(_hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        _ = GetMonitorInfo(hmon, ref mi);

        var abd = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uEdge = ABE_LEFT,
            rc = new RECT { left = mi.rcMonitor.left, top = mi.rcMonitor.top, right = widthPx, bottom = mi.rcMonitor.bottom },
        };
        _ = SHAppBarMessage(ABM_QUERYPOS, ref abd);
        _ = SHAppBarMessage(ABM_SETPOS, ref abd);

        _ = SetWindowPos(_hwnd, default, abd.rc.left, abd.rc.top,
            abd.rc.right - abd.rc.left, abd.rc.bottom - abd.rc.top,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        _hidden = hidden;
    }
}
