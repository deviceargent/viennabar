using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;
using Shell = ViennaBar.ShellNative.ShellNative;

namespace ViennaBar;

// Shell.Host — ventana AppBar con auto-hide, layout de 3 tercios, render D2D.
internal sealed unsafe class App : IDisposable
{
    private const string WndClass = "ViennaBarSidebar";
    private const int FullWidthPx = 280;
    private const int SliverPx = 2;
    private const nuint TimerReveal = 1;
    private const nuint TimerHide = 2;
    private const nuint TimerDrawer = 4;   // auto-close del drawer (4s idle)
    private const uint RevealDelayMs = 80;
    private const uint HideDelayMs = 400;

    private HWND _hwnd;
    private bool _hidden = true;
    private bool _drawerOpen;
    private bool _dragActive;   // drag OLE en curso: suspender auto-hide (WM_MOUSELEAVE espurio)
    private uint _dpi = 96;

    private readonly ShellTree _tree;
    private DropEngine _drop = null!;
    private readonly Drawer _drawer;
    private readonly Widgets _widgets = new();
    private readonly Renderer _renderer = new();

    private int ClientH => Math.Max(1, _clientH);
    private int _clientH;
    // layout: widgets compacto fijo, drawer fijo, tree flexible (come el resto).
    // El panel del widgets murio en F2.5: su tercio quedaba 2/3 vacio.
    private int WidgetsH => 190;
    private int DrawerH => 344;
    private int TreeH => Math.Max(150, ClientH - WidgetsH - DrawerH);

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

        // F3/M1: aplicar/revertir override de verbs (sin ventana)
        if (args.Length >= 1 && args[0] == "--m1-apply")
        {
            Console.WriteLine(Integration.ApplyM1(Microsoft.Win32.Registry.CurrentUser,
                @"Software\Classes", @"Software\ViennaBar\M1Backup",
                Environment.ProcessPath ?? "ViennaBar.exe", DesktopRescuePath()));
            Shell.NotifyAssocChanged();
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--m1-revert")
        {
            Console.WriteLine(Integration.RevertM1(Microsoft.Win32.Registry.CurrentUser,
                @"Software\Classes", @"Software\ViennaBar\M1Backup"));
            Shell.NotifyAssocChanged();
            return 0;
        }
        // F3/M2: IFEO sobre explorer.exe (REQUIERE terminal elevada; sin ventana).
        // El apply vivo es prueba MANUAL con el usuario presente (admin + riesgo).
        if (args.Length >= 1 && args[0] == "--m2-apply")
        {
            string dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            Console.WriteLine(Integration.ApplyM2(Microsoft.Win32.Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
                Microsoft.Win32.Registry.CurrentUser, @"Software\ViennaBar\M2Backup",
                System.IO.Path.Combine(dir, "ViennaBar.Launcher.exe"),
                System.IO.Path.Combine(dir, "explorer-vb.exe"),
                @"C:\Windows\explorer.exe", DesktopRescuePath("ViennaBar-M2-revert.reg")));
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--m2-revert")
        {
            Console.WriteLine(Integration.RevertM2(Microsoft.Win32.Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
                Microsoft.Win32.Registry.CurrentUser, @"Software\ViennaBar\M2Backup"));
            return 0;
        }

        // ProtocolRouter: --open-folder <path> (M1 lo invoca por doble-click)
        if (args.Length >= 2 && args[0] == "--open-folder")
        {
            // instancia viva? reenviar y salir
            if (SingleInstance.ForwardOpenFolder(args[1])) return 0;
            s_pendingOpenFolder = args[1];   // primario: arrancar y navegar
        }
        if (!SingleInstance.Acquire()) return 0;

        ViennaBar.ShellNative.ShellNative.ClearDropSnapshots();   // stack en memoria: snapshots huerfanos fuera
        _ = Config.LoadDefault();   // settings + hot-reload watcher (antes que Skin)
        _ = Skin.LoadDefault();   // tokens + hot-reload watcher
        var tree = new ShellTree();
        var drawer = new Drawer();
        var app = new App(tree, drawer);
        Instance = app;
        return app.MessageLoop();
    }

    private static string? s_pendingOpenFolder;

    private static string DesktopRescuePath(string file = "ViennaBar-M1-revert.reg") =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), file);

    // hot-reload de skin: recrea brushes con tokens nuevos y repinta
    internal void ReloadSkin(Skin skin)
    {
        // cross-thread: post al hilo UI
        if (_hwnd != default)
        {
            _ = PostMessage(_hwnd, WM_APP + 2, 0, 0); // WM_APP+2 = reload skin
        }
    }

    // aplicar config editado a mano: re-lee geometría en el hilo UI
    internal void RequestConfigApply()
    {
        if (_hwnd != default)
        {
            _ = PostMessage(_hwnd, WM_APP + 3, 0, 0); // WM_APP+3 = apply config
        }
    }

    // drag OLE en curso (llamado desde DropEngine CCW): sin cross-thread —
    // DragEnter/Over/Leave/Drop llegan en el hilo STA = hilo UI.
    internal void SetDragActive(bool active)
    {
        _dragActive = active;
        _drawer.SetDragOver(active);   // feedback visual de la zona de drop
        if (active)
        {
            if (_hidden) SetPos(FullWidthPx, hidden: false);   // revelar ante drag externo
            else _ = KillTimer(_hwnd, TimerHide);   // cancelar hide ya programado
        }
    }

    private int MessageLoop()
    {
        AppLog("ml: begin");
        CrashDiag.Init();
        AppLog("ml: crashdiag init");
        var hinst = GetModuleHandle(default(PCWSTR));
        AppLog("ml: hinst");

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
            // sin WS_EX_NOACTIVATE: el click activa la ventana y da foco de
            // teclado (busqueda del drawer). El reveal por mouse no activa.
            _hwnd = CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST,
                wc, title,
                WINDOW_STYLE.WS_POPUP,
                -FullWidthPx, 0, FullWidthPx, _clientH,
                default, default, hinst, null);
        }
        if (_hwnd.IsNull) return 1;
        AppLog("ml: window created");

        // renderer ANTES del primer SetPos (que llama Resize)
        _renderer.Init();
        AppLog("ml: renderer init");
        AppText.Init(_renderer);

        RegisterAppBar();
        AppLog("ml: appbar");
        SetPos(SliverPx, hidden: true);
        AppLog("ml: setpos");
        _ = ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        AppLog("ml: shown");

        _tree.Attach(_hwnd);
        AppLog("tree attached");
        _drop = new DropEngine(_tree);
        // M1: arranque via --open-folder sin instancia previa: revelar y navegar
        if (s_pendingOpenFolder is not null)
        {
            SetPos(FullWidthPx, hidden: false);
            _tree.ExpandToPath(s_pendingOpenFolder);
            AppLog($"open-folder: {s_pendingOpenFolder}");
            s_pendingOpenFolder = null;
        }
        _drop.Attach(_hwnd);
        AppLog("drop attached");
        _drawer.Attach(_hwnd);
        AppLog("drawer attached");
        _drawer.InitText(_renderer);
        // widgets: timer solo con la barra visible (Start/Stop en SetPos)
        _widgets.Start(_hwnd);
        _drawer.SetDropStack(_drop.DropStack);   // superficie drop en el drawer colapsado
        AppLog("widgets started");

        MSG msg = default;
        int r;
        while ((r = (int)GetMessage(out msg, default, 0, 0)) != 0)
        {
            if (r == -1) { AppLog("ml: GetMessage -1 (error)"); break; }
            TranslateMessage(in msg);
            _ = DispatchMessage(in msg);
        }
        AppLog($"ml: GetMessage exit (r={r}, last msg=0x{msg.message:X})");
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
        AppLog($"setpos: abd.rc = L{abd.rc.left} T{abd.rc.top} R{abd.rc.right} B{abd.rc.bottom} (w={widthPx}, hidden={hidden})");

        // el alto REAL es el del rect appbar (work area sin taskbar), NO el del
        // monitor: de el derivan los tercios (un shift de 48px escondia el boton)
        _clientH = abd.rc.bottom - abd.rc.top;

        _ = SetWindowPos(_hwnd, default, abd.rc.left, abd.rc.top,
            abd.rc.right - abd.rc.left, abd.rc.bottom - abd.rc.top,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        _hidden = hidden;

        // timer de widgets: activo SOLO revelado (idle oculto = 0 CPU absoluto)
        if (hidden) _ = KillTimer(_hwnd, 3);
        else _ = SetTimer(_hwnd, 3, 1000, null);

        // el RT de D2D sigue el tamaño de la ventana
        _renderer.Resize((nint)_hwnd.Value, (uint)(abd.rc.right - abd.rc.left), (uint)(abd.rc.bottom - abd.rc.top));
        _widgets.OnRendererReset();   // handles de thumbs del clip mueren con el RT
        _ = InvalidateRect(_hwnd, (RECT*)null, true);
    }

    public void Invalidate() => _ = InvalidateRect(_hwnd, (RECT*)null, false);

    private bool Hidden => _hidden;

    // cursor fisicamente dentro del rect de la ventana (anti LEAVE espurio)
    private bool CursorInsideWindow()
    {
        _ = GetCursorPos(out var pt);
        _ = GetWindowRect(_hwnd, out var r);
        return pt.X >= r.left && pt.X < r.right && pt.Y >= r.top && pt.Y < r.bottom;
    }

    // ---- drop deferral: soltar sobre carpeta del tree pregunta que hacer ----
    private (List<string> paths, string dest)? _pendingDrop;

    // pt en coords de pantalla (las que entrega OLE). Si cae sobre una
    // carpeta del tree: stash + menu async. Si no: apila como antes.
    internal void DeferDrop(List<string> paths, int screenX, int screenY)
    {
        var pt = new System.Drawing.Point(screenX, screenY);
        _ = ScreenToClient(_hwnd, ref pt);
        if (pt.Y >= WidgetsH && pt.Y < WidgetsH + TreeH)
        {
            var node = _tree.HitTestNode(pt.Y - WidgetsH, FullWidthPx, TreeH);
            if (node is not null && node.IsFolder && node.ParsingName.Length > 0)
            {
                AppLog($"defer: {paths.Count} sobre carpeta {node.Name}");
                _pendingDrop = (paths, node.ParsingName);
                _ = PostMessage(_hwnd, WM_APP + 4, 0, 0); // WM_APP+4 = drop menu
                return;
            }
        }
        _drop.StackPaths(paths);
    }

    private void ShowDropMenu(List<string> paths, string dest)
    {
        var hmenu = CreatePopupMenu();
        fixed (char* m1 = "Mover aquí", m2 = "Copiar aquí", m3 = "Apilar")
        {
            _ = AppendMenu(hmenu, default, 1, m1);
            _ = AppendMenu(hmenu, default, 2, m2);
            _ = AppendMenu(hmenu, default, 3, m3);
        }
        // menu programatico (sin click previo que active): SetForegroundWindow
        // es OBLIGATORIO o el menu aparece muerto (no recibe input). Como somos
        // proceso de fondo, el anti-focus-stealing lo niega: puente con
        // AttachThreadInput al thread foreground (patron documentado).
        // WM_NULL post-cierre evita el menu fantasma.
        try
        {
            var fg = GetForegroundWindow();
            if (!fg.IsNull)
            {
                uint fgId = GetWindowThreadProcessId(fg, null);
                uint ourId = GetWindowThreadProcessId(_hwnd, null);
                if (fgId != ourId && ourId != 0)
                {
                    _ = AttachThreadInput(ourId, fgId, true);
                    _ = SetForegroundWindow(_hwnd);
                    _ = AttachThreadInput(ourId, fgId, false);
                }
                else _ = SetForegroundWindow(_hwnd);
            }
        }
        catch { }
        _ = GetCursorPos(out var pt);
        int cmd = TrackPopupMenu(hmenu,
            TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON,
            pt.X, pt.Y, 0, _hwnd, null);
        _ = DestroyMenu(hmenu);
        _ = PostMessage(_hwnd, 0, 0, 0);   // WM_NULL
        if (cmd == 1 || cmd == 2)
        {
            // UI de progreso del shell + deshacer (ALLOWUNDO); el tree se
            // refresca solo via SHChangeNotify
            int rc = Shell.FileOperation((nint)_hwnd.Value, cmd == 1 ? Shell.FO_MOVE : Shell.FO_COPY,
                paths, dest, Shell.FOF_ALLOWUNDO);
            AppLog($"dropmenu: cmd={cmd} dest={dest} rc=0x{rc:X}");
            if (rc == 0) _tree.ExpandToPath(dest);   // feedback: mostrar el destino
        }
        else if (cmd == 3)
        {
            _drop.StackPaths(paths);
        }
        else AppLog("dropmenu: cancelado");
        Invalidate();
    }

    // ---- drag-out del Drop Stack ----
    private int _pressIdx = -1;                 // item del stack con boton izq abajo
    private (int, int) _pressPt;
    private ViennaBar.ShellNative.DropSourceCcw? _dropSource;

    // arranca DoDragDrop con el item idx del stack. Corre en el hilo UI
    // (STA) — modal hasta soltar.
    private void StartDragOut(int idx)
    {
        var stack = _drop.DropStack;
        if (idx < 0 || idx >= stack.Count) return;
        var path = stack[idx];
        AppLog($"dragout: begin idx={idx} path={path}");

        var dataObj = ViennaBar.ShellNative.ShellNative.CreateDataObjectFromPaths(new[] { path });
        if (dataObj == 0) { AppLog("dragout: CreateDataObject FAIL"); return; }

        _dropSource ??= new ViennaBar.ShellNative.DropSourceCcw();

        // COPY | MOVE | LINK: el target decide; con MOVE (e.g. mover a otra
        // carpeta) sacamos el item del stack
        var (hr, effect) = ViennaBar.ShellNative.ShellNative.DragOut(dataObj, _dropSource.IUnknownPtr, 7);
        ViennaBar.ShellNative.ShellNative.ReleaseDataObject(dataObj);
        AppLog($"dragout: DoDragDrop hr=0x{hr:X} effect={effect}");

        if (hr == 0x00040100 /* DRAGDROP_S_DROP */ && (effect & 2) != 0 /* MOVE */)
        {
            stack.RemoveAt(idx);
            Invalidate();
        }
    }

    internal static void AppLog(string s) =>
        ViennaBar.ShellNative.ShellNative.DebugLog(s);

    private void OnClick(int x, int y)
    {
        if (y >= WidgetsH + TreeH)
        {
            if (y >= ClientH - 40 || !_drawerOpen)
            {
                _drawerOpen = !_drawerOpen;
                if (_drawerOpen)
                {
                    _drawer.FocusSearch();
                    ArmDrawerTimer();
                }
                else _ = KillTimer(_hwnd, TimerDrawer);
                Invalidate();
            }
            else
            {
                _drawer.OnClick(x, y - WidgetsH - TreeH, DrawerH);
                if (_drawer.ConsumeDismiss()) CloseDrawer();
                else { ArmDrawerTimer(); Invalidate(); }
            }
        }
        else if (y >= WidgetsH)
        {
            _tree.OnClick(x, y - WidgetsH, FullWidthPx, TreeH);
        }
        else
        {
            // tercio widgets: click en linea del clip = restaurar al portapapeles
            int ci = _widgets.ClipHitTest(y);
            if (ci >= 0) _widgets.RestoreClip(ci);
            else
            {
                int ii = _widgets.ImgHitTest(x, y);
                if (ii >= 0) _widgets.RestoreClipImage(ii);
            }
        }
    }

    private void CloseDrawer()
    {
        _drawerOpen = false;
        _ = KillTimer(_hwnd, TimerDrawer);
        Invalidate();
    }

    private void ArmDrawerTimer()
    {
        _ = KillTimer(_hwnd, TimerDrawer);
        _ = SetTimer(_hwnd, TimerDrawer, 4000, null);
    }

    // teclado → drawer (busqueda + navegacion). Llega porque al hacer click la
    // ventana se activa y gana foco.
    private void OnKey(uint msg, WPARAM wparam)
    {
        if (!_drawerOpen) return;
        bool handled = _drawer.OnKey(msg, wparam);
        if (_drawer.ConsumeDismiss()) CloseDrawer();
        else if (handled) { ArmDrawerTimer(); Invalidate(); }
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

            case WM_MOUSELEAVE:
                // durante un drag OLE el capture se va al drag helper y llegan
                // WM_MOUSELEAVE espurios → NO ocultar la barra en mitad de un drop
                if (!_hidden && !_dragActive)
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
                        // el LEAVE puede haber sido espurio (drag OLE / capture):
                        // solo ocultar si el cursor salio de verdad (y no en modo foto)
                        if (!_hidden && !Config.Current.StayOpen && !CursorInsideWindow())
                        {
                            SetPos(SliverPx, hidden: true);
                        }
                        break;

                    case 3: // widgets tick (solo con barra visible)
                        _widgets.OnTimer(hwnd, 3);
                        _drop.PruneMissing();
                        break;

                    case TimerDrawer: // auto-close del drawer (idle)
                        _ = KillTimer(hwnd, TimerDrawer);
                        if (_drawerOpen)
                        {
                            _drawerOpen = false;
                            Invalidate();
                        }
                        break;
                }
                return default;

            case WM_LBUTTONUP:
            {
                AppLog($"click L at {GET_X_LPARAM(lparam)},{GET_Y_LPARAM(lparam)} (widgetsH={WidgetsH}, treeH={TreeH})");
                if (_pressIdx >= 0)
                {
                    // press de drag-out sin movimiento: no-op (no togglear nada)
                    ReleaseCapture();
                    _pressIdx = -1;
                    _drawer.SetPressedItem(-1);
                }
                else if (_pressIdx == -2) _pressIdx = -1;   // × consumido: no-op
                else OnClick(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
                return default;
            }

            case WM_LBUTTONDOWN:
            {
                int px = GET_X_LPARAM(lparam), py = GET_Y_LPARAM(lparam);
                if (py >= WidgetsH + TreeH && !_drawerOpen)
                {
                    int ly = py - WidgetsH - TreeH;
                    // × primero: desapila al instante (sin press, sin drag)
                    int rx = _drawer.ThumbRemoveHitTest(px, ly);
                    if (rx >= 0)
                    {
                        _drop.Unstack(rx);
                        _pressIdx = -2;   // consumido: el UP no debe togglear nada
                        return default;
                    }
                    // drawer colapsado: press sobre thumb = potencial drag-out
                    int idx = _drawer.ThumbHitTest(px, ly);
                    if (idx >= 0)
                    {
                        _pressIdx = idx;
                        _pressPt = (px, py);
                        _drawer.SetPressedItem(idx);
                        _ = SetCapture(hwnd);
                    }
                }
                return default;
            }

            case WM_MOUSEMOVE:
                if (_pressIdx >= 0)
                {
                    int px = GET_X_LPARAM(lparam), py = GET_Y_LPARAM(lparam);
                    int dx = px - _pressPt.Item1, dy = py - _pressPt.Item2;
                    if ((dx * dx + dy * dy) > 25)   // umbral 5px (SM_CXDRAG aprox)
                    {
                        var idx = _pressIdx;
                        ReleaseCapture();
                        _pressIdx = -1;
                        _drawer.SetPressedItem(-1);
                        StartDragOut(idx);
                    }
                }
                else if (_hidden)
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

            case WM_CLIPBOARDUPDATE:
                _widgets.OnClipboardUpdate();
                return default;

            case WM_MOUSEWHEEL:
            {
                // scroll del tree (solo sobre su tercio)
                int wy = GET_Y_LPARAM(lparam);
                if (wy >= WidgetsH && wy < WidgetsH + TreeH)
                {
                    short delta = (short)((lparam.Value >> 16) & 0xFFFF);
                    _tree.ScrollBy(-delta / 120 * 3, TreeH);
                }
                return default;
            }

            case WM_APP + 4:
            {
                // drop deferral: menu Mover/Copiar/Apilar (paths ya extraidos
                // en el OnDrop; el IDataObject* original ya no es valido aqui)
                var pd = _pendingDrop;
                _pendingDrop = null;
                if (pd is not null) ShowDropMenu(pd.Value.paths, pd.Value.dest);
                return default;
            }

            case 0x004A: // WM_COPYDATA: --open-folder de una segunda instancia
            {
                var cds = (SingleInstance.CopyDataMsg*)(void*)lparam.Value;
                if (cds->dwData == 1 && cds->cbData >= 2)
                {
                    string path = new string((char*)cds->lpData, 0, (int)(cds->cbData / 2)).TrimEnd('\0');
                    AppLog($"open-folder: {path}");
                    SetPos(FullWidthPx, hidden: false);
                    if (path.Length > 0) _tree.ExpandToPath(path);
                    Invalidate();
                }
                return default;
            }

            case WM_CAPTURECHANGED:
                // perdida de capture ajena (p.ej. ventana popup): cancelar press
                _pressIdx = -1;
                _drawer.SetPressedItem(-1);
                return default;

            case WM_CHAR:
                OnKey(WM_CHAR, wparam);
                return default;

            case WM_KEYDOWN:
                OnKey(WM_KEYDOWN, wparam);
                return default;

            case WM_RBUTTONUP:
            {
                int rx = GET_X_LPARAM(lparam);
                int ry = GET_Y_LPARAM(lparam);
                AppLog($"click R at {rx},{ry} (widgetsH={WidgetsH})");
                if (ry >= WidgetsH && ry < WidgetsH + TreeH)
                    _tree.OnRightClick(rx, ry - WidgetsH, FullWidthPx, TreeH);
                return default;
            }

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

            case WM_APP + 2:
                // hot-reload skin: recrea brushes con tokens nuevos y repinta
                _renderer.ReloadBrushes(_hwnd, 0, 0);
                Invalidate();
                return default;

            case WM_APP + 3:
                // hot-reload config: re-resuelve skin (pudo cambiar "skin"),
                // re-aplica geometría actual y repinta
                Skin.LoadDefault();
                _renderer.ReloadBrushes(_hwnd, 0, 0);
                SetPos(_hidden ? SliverPx : FullWidthPx, _hidden);
                Invalidate();
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

            ctx.Text("widgets", _renderer.Text9Handle, Skin.Muted, 8, 6, 260, 18);
            _widgets.Render(ctx, 0, 0, FullWidthPx, WidgetsH);

            _tree.Render(ctx, 0, WidgetsH, FullWidthPx, TreeH);
            _drawer.SetDrawerArea(DrawerH);
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
