using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace ViennaBar;

// Shell.Host — ventana AppBar con auto-hide, layout de 3 tercios, render D2D.
internal sealed unsafe class App : IDisposable
{
    private const string WndClass = "ViennaBarSidebar";
    private const int FullWidthPx = 280;
    private const int SliverPx = 2;
    private const nuint TimerReveal = 1;
    private const nuint TimerHide = 2;
    private const uint RevealDelayMs = 80;
    private const uint HideDelayMs = 400;

    private HWND _hwnd;
    private bool _hidden = true;
    private bool _drawerOpen;
    private uint _dpi = 96;

    private readonly ShellTree _tree;
    private DropEngine _drop = null!;
    private readonly Drawer _drawer;
    private readonly Renderer _renderer = new();

    private int ClientH => Math.Max(1, _clientH);
    private int _clientH;
    private int WidgetsH => ClientH / 3;
    private int TreeH => ClientH / 3;
    private int DrawerH => ClientH - WidgetsH - TreeH;

    public static App? Instance { get; private set; }

    private App(ShellTree tree, Drawer drawer)
    {
        _tree = tree;
        _drawer = drawer;
    }

    public static int Run(string[] args)
    {
        _ = OleInitialize();
        _ = CoInitialize(default);

        // ProtocolRouter stub: --open-folder <path> (M1 lo invocará)
        if (args.Length >= 2 && args[0] == "--open-folder")
        {
            Console.WriteLine($"vienna://folder?path={Uri.EscapeDataString(args[1])}");
            return 0;
        }

        var tree = new ShellTree();
        var drawer = new Drawer();
        var app = new App(tree, drawer);
        Instance = app;
        return app.MessageLoop();
    }

    private int MessageLoop()
    {
        var hinst = GetModuleHandle(default(PCWSTR));

        fixed (char* wc = WndClass)
        {
            var wcx = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (WNDPROC)WndProc,
                hInstance = (HINSTANCE)hinst.Value,
                hCursor = LoadCursor(default, IDC_ARROW),
                lpszClassName = wc,
                style = WNDCLASS_STYLES.CS_DBLCLKS,
            };
            _ = RegisterClassEx(in wcx);
        }

        var hmon = MonitorFromWindow(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        _ = GetMonitorInfo(hmon, ref mi);
        _clientH = mi.rcMonitor.bottom - mi.rcMonitor.top;

        fixed (char* wc = WndClass, title = "ViennaBar")
        {
            _hwnd = CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                wc, title,
                WINDOW_STYLE.WS_POPUP,
                -FullWidthPx, 0, FullWidthPx, _clientH,
                default, default, hinst, null);
        }
        if (_hwnd.IsNull) return 1;

        // renderer ANTES del primer SetPos (que llama Resize)
        _renderer.Init();
        AppText.Init(_renderer);

        RegisterAppBar();
        SetPos(SliverPx, hidden: true);
        _ = ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        _tree.Attach(_hwnd);
        _drop = new DropEngine(_tree);
        _drop.Attach(_hwnd);
        _drawer.Attach(_hwnd);
        _drawer.InitText(_renderer);

        while (GetMessage(out var msg, default, 0, 0))
        {
            TranslateMessage(in msg);
            _ = DispatchMessage(in msg);
        }
        return 0;
    }

    private void RegisterAppBar()
    {
        var abd = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = WM_APP,
        };
        _ = SHAppBarMessage(ABM_NEW, ref abd);
    }

    private void UnregisterAppBar()
    {
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        _ = SHAppBarMessage(ABM_REMOVE, ref abd);
    }

    private void SetPos(int widthPx, bool hidden)
    {
        var hmon = MonitorFromWindow(_hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        _ = GetMonitorInfo(hmon, ref mi);
        _dpi = GetDpiForWindow(_hwnd);
        _clientH = mi.rcMonitor.bottom - mi.rcMonitor.top;

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

        // el RT de D2D sigue el tamaño de la ventana
        _renderer.Resize(_hwnd, (uint)(abd.rc.right - abd.rc.left), (uint)(abd.rc.bottom - abd.rc.top));
        _ = InvalidateRect(_hwnd, (RECT*)null, true);
    }

    public void Invalidate() => _ = InvalidateRect(_hwnd, (RECT*)null, false);

    private void OnClick(int x, int y)
    {
        if (y >= WidgetsH + TreeH)
        {
            if (y >= ClientH - 40 || !_drawerOpen)
            {
                _drawerOpen = !_drawerOpen;
                Invalidate();
            }
            else
            {
                _drawer.OnClick(x, y - WidgetsH - TreeH, DrawerH);
            }
        }
        else if (y >= WidgetsH)
        {
            _tree.OnClick(x, y - WidgetsH, FullWidthPx, TreeH);
        }
    }

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wparam, LPARAM lparam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return (LRESULT)1;

            case WM_PAINT:
            {
                var ps = default(PAINTSTRUCT);
                var hdc = BeginPaint(hwnd, out ps);
                _ = EndPaint(hwnd, in ps);   // validate; el contenido lo pinta D2D aparte
                PaintScene();
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
                        if (pt.X <= SliverPx) SetPos(FullWidthPx, hidden: false);
                        break;

                    case TimerHide:
                        _ = KillTimer(hwnd, TimerHide);
                        if (!_hidden) SetPos(SliverPx, hidden: true);
                        break;
                }
                return default;

            case WM_LBUTTONUP:
                OnClick(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
                return default;

            case WM_LBUTTONDBLCLK:
                // kill-switch dev: SOLO con archivo centinela %TEMP%\viennabar-kill
                // (nunca cierra accidentalmente; debug: touch + doble clic en widgets)
                if (GET_Y_LPARAM(lparam) < WidgetsH &&
                    File.Exists(Path.Combine(Path.GetTempPath(), "viennabar-kill")))
                {
                    _ = DestroyWindow(hwnd);
                }
                return default;

            case WM_APP:
                return default;

            case WM_DPICHANGED:
                SetPos(_hidden ? SliverPx : FullWidthPx, _hidden);
                return default;

            case WM_DESTROY:
                UnregisterAppBar();
                _drop?.Detach();
                PostQuitMessage(0);
                return default;
        }
        return DefWindowProc(hwnd, msg, wparam, lparam);
    }

    private void PaintScene()
    {
        if (_hidden) return;
        _renderer.DrawScene(ctx =>
        {
            ctx.Clear(Skin.Bg);

            // sheen Vienna (mitad superior con gradiente): F2 con DComp; F1 dos tonos
            ctx.FillRect(unchecked((int)0xFFC8E0EE), 0, 0, FullWidthPx, WidgetsH / 2f);

            // divisores
            ctx.Line(Skin.Divider, 0, WidgetsH, FullWidthPx, WidgetsH);
            ctx.Line(Skin.Divider, 0, WidgetsH + TreeH, FullWidthPx, WidgetsH + TreeH);

            ctx.Text("widgets", _renderer.Text9, Skin.Muted, 8, 6, 260, 18);

            _tree.Render(ctx, 0, WidgetsH, FullWidthPx, TreeH);
            _drawer.Render(ctx, 0, WidgetsH + TreeH, FullWidthPx, DrawerH, _drawerOpen);
        });
    }

    internal static int GET_X_LPARAM(LPARAM lp) => (int)(short)(lp.Value & 0xFFFF);
    internal static int GET_Y_LPARAM(LPARAM lp) => (int)(short)((lp.Value >> 16) & 0xFFFF);

    public void Dispose()
    {
        _renderer.Dispose();
        _tree.Dispose();
        _drop?.Dispose();
        _drawer.Dispose();
    }
}
