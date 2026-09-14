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

    // P/Invokes power (HKCU no requiere admin; UI thread STA)
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(POWER_STATE state, bool fForce, bool fDisableWakeup);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- shutdown privilege (advapi32): necesario para ExitWindowsEx sin admin ----
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint bufLen, IntPtr prev, IntPtr retLen);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    // EWX flags
    private const uint EWX_LOGOFF = 0x00000000;
    private const uint EWX_SHUTDOWN = 0x00000001;
    private const uint EWX_REBOOT = 0x00000002;
    private const uint EWX_FORCE = 0x00000004;
    private const uint EWX_POWEROFF = 0x00000008;

    private const int HotKeyRotateSkin = 0xB00;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;

    private static bool EnableShutdownPrivilege()
    {
        try
        {
            IntPtr proc = System.Diagnostics.Process.GetCurrentProcess().Handle;
            if (!OpenProcessToken(proc, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
                return false;
            try
            {
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                    return false;
                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
                };
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { _ = Windows.Win32.PInvoke.CloseHandle(new Windows.Win32.Foundation.HANDLE(token)); }
        }
        catch { return false; }
    }

    private void DoPowerAction(int idx)
    {
        const uint MB_YESNO = 0x00000004;
        const uint MB_ICONQUESTION = 0x00000020;
        const uint MB_DEFBUTTON1 = 0x00000000;

        switch (idx)
        {
            case 0: // Apagar el equipo
                if (MessageBoxW(IntPtr.Zero, "¿Apagar el equipo?", "ViennaBar", MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON1) == 6)
                {
                    EnableShutdownPrivilege();
                    if (ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF | EWX_FORCE, 0)) AppLog("power: shutdown initiated");
                    else AppLog("power: shutdown failed, error " + Marshal.GetLastWin32Error());
                }
                else AppLog("power: shutdown cancelled");
                break;
            case 1: // Reiniciar el equipo
                if (MessageBoxW(IntPtr.Zero, "¿Reiniciar el equipo?", "ViennaBar", MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON1) == 6)
                {
                    EnableShutdownPrivilege();
                    if (ExitWindowsEx(EWX_REBOOT | EWX_FORCE, 0)) AppLog("power: reboot initiated");
                    else AppLog("power: reboot failed, error " + Marshal.GetLastWin32Error());
                }
                else AppLog("power: reboot cancelled");
                break;
            case 2: // Suspender
                if (MessageBoxW(IntPtr.Zero, "¿Suspender el equipo?", "ViennaBar", MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON1) == 6)
                {
                    if (SetSuspendState(POWER_STATE.Suspend, false, false)) AppLog("power: suspend initiated");
                    else AppLog("power: suspend failed, error " + Marshal.GetLastWin32Error());
                }
                else AppLog("power: suspend cancelled");
                break;
            case 3: // Cerrar sesión
                EnableShutdownPrivilege();
                if (ExitWindowsEx(EWX_LOGOFF | EWX_FORCE, 0)) AppLog("power: logout initiated");
                else AppLog("power: logout failed, error " + Marshal.GetLastWin32Error());
                break;
            case 4: // Bloquear estación
                LockWorkStation();
                AppLog("power: lock station");
                break;
        }
    }

    private enum POWER_STATE : uint
    {
        Suspend = 1,
        Standby = 2,
        Critical = 4
    }

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
    private readonly ColorPicker _picker = new();
    private bool _pickerOpen;
    private int _pickerDrag;   // 0=none, 1=slider, 2=wheel (para arrastre continuo)
    private float _wheelCx = 140, _wheelCy = 302, _wheelR = 72;   // centro/radio de la rueda grande (default, se recalcula en paint)

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
        bool hk = RegisterHotKey(_hwnd, HotKeyRotateSkin, MOD_CONTROL | MOD_SHIFT, 0x54);   // Ctrl+Shift+T
        AppLog($"ml: hotkey registered={hk} err={Marshal.GetLastWin32Error()}");

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
        _renderer.SetSkinLogo(Skin.ActiveLogoPath());
        _picker.SyncFromConfig();
        _drawer.SetPicker(_picker);
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
        _picker.OnRendererReset();     // bitmap del disco de la rueda muere con el RT
        _ = InvalidateRect(_hwnd, (RECT*)null, true);

        ApplyRoundedRegion((int)(abd.rc.right - abd.rc.left), (int)(abd.rc.bottom - abd.rc.top));
    }

    // esquinas redondeadas de la ventana. Solo cuando hay ancho real (revelado);
    // el sliver de 2px se deja recto (no se nota y evita regiones degeneradas).
    private void ApplyRoundedRegion(int w, int h)
    {
        const int radius = 12;
        var region = w > 20
            ? CreateRoundRectRgn(0, 0, w + 1, h + 1, radius, radius)
            : CreateRoundRectRgn(0, 0, w + 1, h + 1, 0, 0);
        SetWindowRgn(_hwnd, region, new Windows.Win32.Foundation.BOOL(1));
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
        if (_dragOutInProgress) return;   // drop interno del propio drag-out: no duplicar
        var pt = new System.Drawing.Point(screenX, screenY);
        _ = ScreenToClient(_hwnd, ref pt);
        if (pt.Y >= WidgetsH && pt.Y < WidgetsH + TreeH)
        {
            var node = _tree.HitTestNode(pt.Y - WidgetsH, FullWidthPx, TreeH);
            if (node is not null && node.IsFolder && node.ParsingName.Length > 0)
            {
                AppLog($"defer: {paths.Count} sobre carpeta {node.Name}");
                _pendingDrop = (paths, node.ParsingName);
                _dropMenuAt = (pt.X, pt.Y);
                _ = PostMessage(_hwnd, WM_APP + 4, 0, 0); // WM_APP+4 = drop menu
                return;
            }
        }
        _drop.StackPaths(paths);
    }

    // ---- menu deferral PROPIO (D2D, sin TrackPopupMenu) ----
    // Razon: el menu nativo programatico necesita foreground (que el
    // anti-focus-stealing niega) y el puente AttachThreadInput puede wedgiar
    // el hilo UI contra un thread colgado. El menu propio solo usa nuestro
    // WndProc: imposible de bloquear desde otro proceso.
    private bool _dropMenuOpen;
    private (int x, int y) _dropMenuAt;
    private int _dropMenuSel;
    private const float DropMenuW = 190f;
    private const float DropMenuRowH = 26f;

    private void ShowDropMenu()
    {
        if (_pendingDrop is null) return;
        _pressIdx = -1;
        _drawer.SetPressedItem(-1);
        _dropMenuOpen = true;
        _dropMenuSel = 0;
        _ = SetCapture(_hwnd);
        Invalidate();
    }

    private int DropMenuRows => 3;

    private (float x, float y, float w, float h) DropMenuRect()
    {
        float w = DropMenuW;
        float h = 12 + DropMenuRows * DropMenuRowH;
        float x = Math.Clamp(_dropMenuAt.Item1 - 20, 4, FullWidthPx - w - 4);
        float y = Math.Clamp(_dropMenuAt.Item2 - 10, WidgetsH + 4, ClientH - h - 4);
        return (x, y, w, h);
    }

    private int DropMenuHitTest(int x, int y)
    {
        var (mx, my, mw, mh) = DropMenuRect();
        if (x < mx || x >= mx + mw || y < my + 6 || y >= my + mh - 6) return -1;
        int row = (int)((y - (my + 6)) / DropMenuRowH);
        return row >= 0 && row < DropMenuRows ? row : -1;
    }

    private void CloseDropMenu() => _ = CloseDropMenu(0);

    // cmd: 0=cancelar, 1=mover, 2=copiar, 3=apilar
    private bool CloseDropMenu(int cmd)
    {
        if (!_dropMenuOpen) return false;
        _dropMenuOpen = false;
        ReleaseCapture();
        var pd = _pendingDrop;
        _pendingDrop = null;
        if (pd is null) { Invalidate(); return true; }
        if (cmd == 1 || cmd == 2)
        {
            // UI de progreso del shell + deshacer (ALLOWUNDO); el tree se
            // refresca solo via SHChangeNotify
            int rc = Shell.FileOperation((nint)_hwnd.Value, cmd == 1 ? Shell.FO_MOVE : Shell.FO_COPY,
                pd.Value.paths, pd.Value.dest, Shell.FOF_ALLOWUNDO);
            AppLog($"dropmenu: cmd={cmd} dest={pd.Value.dest} rc=0x{rc:X}");
            if (rc == 0) _tree.ExpandToPath(pd.Value.dest);   // feedback: mostrar el destino
        }
        else if (cmd == 3)
        {
            _drop.StackPaths(pd.Value.paths);
        }
        else AppLog("dropmenu: cancelado");
        Invalidate();
        return true;
    }

    private void PaintDropMenu(RenderCtx ctx)
    {
        if (!_dropMenuOpen) return;
        var (mx, my, mw, mh) = DropMenuRect();
        ctx.FillRect(Skin.Search, mx, my, mw, mh);
        ctx.Line(Skin.Divider, mx, my, mx + mw, my);
        ctx.Line(Skin.Divider, mx, my + mh, mx + mw, my + mh);
        ctx.Line(Skin.Divider, mx, my, mx, my + mh);
        ctx.Line(Skin.Divider, mx + mw, my, mx + mw, my + mh);
        string[] items = ["Mover aquí", "Copiar aquí", "Apilar"];
        for (int i = 0; i < items.Length; i++)
        {
            float ry = my + 6 + i * DropMenuRowH;
            if (i == _dropMenuSel)
                ctx.FillRect(Skin.Sel, mx + 4, ry - 1, mw - 8, DropMenuRowH);
            ctx.Text(items[i], AppText.FmtBig, Skin.Text, mx + 12, ry + 3, mw - 24, 18);
        }
    }

    // ---- drag-out del Drop Stack ----
    private int _pressIdx = -1;                 // item del stack con boton izq abajo
    private (int, int) _pressPt;
    private ViennaBar.ShellNative.DropSourceCcw? _dropSource;
    private bool _dragOutInProgress;   // drag-out propio en curso: ignorar el drop interno (anti duplicado)

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
        _dragOutInProgress = true;
        var (hr, effect) = ViennaBar.ShellNative.ShellNative.DragOut(dataObj, _dropSource.IUnknownPtr, 7);
        _dragOutInProgress = false;
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
        // selector de color abierto: consome todos los clics
        if (_pickerOpen)
        {
            // cualquier click dentro del velo del selector (área del tree) se consume:
            // no atraviesa y no interfiere con el tree debajo.
            if (y >= WidgetsH && y < WidgetsH + TreeH)
            {
                bool consumed = PickerClick(x, y);
                if (!consumed) Invalidate();   // click en el velo (no en control): nada, mantener abierto
                return;
            }
            ClosePicker();
            return;
        }

        if (y >= WidgetsH + TreeH)
        {
            // drawer colapsado: fila del boton Inicio + iconos power a su derecha
            if (!_drawerOpen && y >= WidgetsH + TreeH + DrawerH - 36 - 4)
            {
                if (_drawer.MiniWheelHitTest(x, 0))
                {
                    OpenPicker();
                    return;
                }
                int pi = _drawer.PowerIconHitTest(x, 0);
                if (pi >= 0)
                {
                    DoPowerAction(pi);
                    return;
                }
            }
            if (y >= ClientH - 40 || !_drawerOpen)
            {
                bool wasOpen = _drawerOpen;
                _drawerOpen = !_drawerOpen;
                if (_drawerOpen)
                {
                    // si el click que abre cae en la caja, enfoca de una
                    // (si no, el toggle se come el click de foco)
                    if (!wasOpen) _drawer.FocusBoxIfHit(y - WidgetsH - TreeH);
                    ArmDrawerTimer(10000);   // gracia inicial para leer; luego 4s por interaccion
                }
                else
                {
                    _drawer.ClearSearchFocus();
                    _ = KillTimer(_hwnd, TimerDrawer);
                }
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
        _drawer.ClearSearchFocus();
        _ = KillTimer(_hwnd, TimerDrawer);
        Invalidate();
    }

    private void ArmDrawerTimer(uint ms = 4000)
    {
        _ = KillTimer(_hwnd, TimerDrawer);
        _ = SetTimer(_hwnd, TimerDrawer, ms, null);
    }

    // teclado del menu deferral (cuando esta abierto consume todo)
    private void DropMenuKey(uint msg, WPARAM wparam)
    {
        if (msg != WM_KEYDOWN) return;
        int vk = (int)wparam.Value;
        if (vk == 0x26) { _dropMenuSel = (_dropMenuSel + 2) % 3; Invalidate(); }        // UP
        else if (vk == 0x28) { _dropMenuSel = (_dropMenuSel + 1) % 3; Invalidate(); }   // DOWN
        else if (vk == 0x0D) CloseDropMenu(_dropMenuSel + 1);                            // ENTER
        else if (vk == 0x1B) CloseDropMenu(0);                                          // ESC
    }

    // teclado → drawer (busqueda + navegacion). Llega porque al hacer click la
    // ventana se activa y gana foco.
    private void OnKey(uint msg, WPARAM wparam)
    {
        if (_dropMenuOpen) { DropMenuKey(msg, wparam); return; }
        if (_drawerOpen)
        {
            bool handled = _drawer.OnKey(msg, wparam);
            if (_drawer.ConsumeDismiss()) CloseDrawer();
            else if (handled) { ArmDrawerTimer(); Invalidate(); }
            return;
        }
        if (_tree.OnFindKey(msg, wparam)) Invalidate();
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
                _tree.SetHover(null);
                _drawer.HoverRow(-1);
                if (!_hidden && !_dragActive && !_pickerOpen)
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
                        if (!_hidden && !Config.Current.StayOpen && !_pickerOpen && !CursorInsideWindow())
                        {
                            CloseDropMenu(0);
                            SetPos(SliverPx, hidden: true);
                        }
                        break;

                    case 3: // widgets tick (solo con barra visible)
                        _widgets.OnTimer(hwnd, 3);
                        _drop.PruneMissing();
                        break;

                    case TimerDrawer: // auto-close del drawer (siempre: mata hasta el foco)
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
                if (_pickerDrag != 0)
                {
                    _pickerDrag = 0;
                    ReleaseCapture();
                    return default;
                }
                if (_dropMenuOpen)
                {
                    // menu deferral: UP dentro de una fila ejecuta, afuera cancela
                    int row = DropMenuHitTest(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
                    CloseDropMenu(row >= 0 ? row + 1 : 0);
                    return default;
                }
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
                // selector abierto: arranca drag de slider/rueda si el press cae dentro
                if (_pickerOpen)
                {
                    if (ValueSliderRect(out int vx, out int vy, out int vw, out int vh) &&
                        px >= vx && px < vx + vw && py >= vy && py < vy + vh)
                    {
                        _pickerDrag = 1;
                        _ = SetCapture(hwnd);
                        PickerDragAt(px, py);
                        return default;
                    }
                    if (_picker.HitWheel(_wheelCx, _wheelCy, _wheelR, px, py, out var hh, out var ss))
                    {
                        _pickerDrag = 2;
                        _ = SetCapture(hwnd);
                        PickerDragAt(px, py);
                        return default;
                    }
                }
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
                if (_pickerDrag != 0)
                {
                    int px = GET_X_LPARAM(lparam), py = GET_Y_LPARAM(lparam);
                    PickerDragAt(px, py);
                    return default;
                }
                if (_dropMenuOpen)
                {
                    int row = DropMenuHitTest(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
                    if (row >= 0 && row != _dropMenuSel) { _dropMenuSel = row; Invalidate(); }
                }
                else if (_pressIdx >= 0)
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
                else if (_pickerOpen)
                {
                    // selector de color abierto: sin hover del tree/drawer debajo
                    _tree.SetHover(null);
                    _drawer.HoverRow(-1);
                }
                else
                {
                    // hover sigue al mouse (highlight, sin tocar seleccion de teclado)
                    int hx = GET_X_LPARAM(lparam), hy = GET_Y_LPARAM(lparam);
                    if (hy >= WidgetsH && hy < WidgetsH + TreeH)
                    {
                        _tree.SetHover(_tree.HitTestNode(hy - WidgetsH, FullWidthPx, TreeH));
                        _drawer.HoverRow(-1);
                    }
                    else if (hy >= WidgetsH + TreeH && _drawerOpen)
                    {
                        _tree.SetHover(null);
                        if (_drawer.HoverRow(_drawer.RowAt(hy - WidgetsH - TreeH))) ArmDrawerTimer();
                    }
                    else
                    {
                        _tree.SetHover(null);
                        _drawer.HoverRow(-1);
                    }
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
                // scroll: drawer abierto sobre su tercio, si no el tree.
                // OJO: el delta va en WPARAM (HIWORD), lParam trae coords.
                int wy = GET_Y_LPARAM(lparam);
                short delta = (short)((wparam.Value >> 16) & 0xFFFF);
                if (wy >= WidgetsH + TreeH && _drawerOpen)
                {
                    _drawer.ScrollBy(-delta / 120 * 3);
                    ArmDrawerTimer();   // scrollear es usar: no cerrar encima
                }
                else if (wy >= WidgetsH && wy < WidgetsH + TreeH)
                    _tree.ScrollBy(-delta / 120 * 3, TreeH);
                return default;
            }

            case WM_APP + 4:
            {
                // drop deferral: abre el menu propio (lee el stash al cerrar)
                if (_pendingDrop is not null) ShowDropMenu();
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
                // menu abierto sin capture = cancelar; si no, cancelar press
                if (_dropMenuOpen) { CloseDropMenu(0); return default; }
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

            case 0x0312: // WM_HOTKEY
                AppLog($"hotkey: WM_HOTKEY wparam=0x{(int)wparam.Value:X}");
                if ((int)wparam.Value == HotKeyRotateSkin)
                {
                    try { RotateSkin(); Invalidate(); }
                    catch (Exception ex) { AppLog($"hotkey: RotateSkin error {ex.Message}"); }
                }
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
                _renderer.SetSkinLogo(Skin.ActiveLogoPath());
                Invalidate();
                return default;

            case WM_APP + 3:
                // hot-reload config: re-resuelve skin (pudo cambiar "skin"),
                // re-aplica geometría actual y repinta
                Skin.LoadDefault();
                _renderer.ReloadBrushes(_hwnd, 0, 0);
                _renderer.SetSkinLogo(Skin.ActiveLogoPath());
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

    private void RotateSkin()
    {
        string[] skins = { "default", "ViennaNight", "ViennaDusk" };
        string current = Config.Current.Skin;
        int nextIdx = Array.IndexOf(skins, current) + 1;
        if (nextIdx >= skins.Length) nextIdx = 0;
        Config.Current.Skin = skins[nextIdx];
        App.AppLog($"rotateSkin: -> {Config.Current.Skin}");

        ReapplySkin();
        PersistConfig();
    }

    private void ReapplySkin()
    {
        Skin.LoadDefault();
        _picker.SyncFromConfig();
        _renderer.ReloadBrushes(_hwnd, 0, 0);
        _renderer.SetSkinLogo(Skin.ActiveLogoPath());
        SetPos(_hidden ? SliverPx : FullWidthPx, _hidden);
        Invalidate();
    }

    // ---- selector de color (rueda cromática) ----
    private void OpenPicker()
    {
        var (h, s, v) = ColorPicker.ArgbToHsv(_picker.SlotColor[_picker.ActiveSlot]);
        _picker.Hue = h; _picker.Sat = s; _picker.Value = v;
        _pickerOpen = true;
        Invalidate();
    }

    private void ClosePicker()
    {
        _pickerOpen = false;
        Invalidate();
    }

    // devuelve true si el clic cayó dentro del selector (y fue consumido)
    private bool PickerClick(int x, int y)
    {
        return PickerDragAt(x, y);
    }

    // aplica color según la posición (slider de valor o rueda). true = consumido.
    private bool PickerDragAt(int x, int y)
    {
        // slider de valor (brillo): vertical a la derecha de la rueda
        if (ValueSliderRect(out int vx, out int vy, out int vw, out int vh) &&
            x >= vx && x < vx + vw && y >= vy && y < vy + vh)
        {
            _picker.Value = Math.Clamp(1.0 - (double)(y - vy) / vh, 0.0, 1.0);
            ApplyPickerColor(_picker.ActiveSlot, ColorPicker.HsvToArgb(_picker.Hue, _picker.Sat, _picker.Value));
            return true;
        }
        // rueda grande
        if (_picker.HitWheel(_wheelCx, _wheelCy, _wheelR, x, y, out double hue, out double sat))
        {
            _picker.Hue = hue;
            _picker.Sat = sat;
            ApplyPickerColor(_picker.ActiveSlot, ColorPicker.HsvToArgb(hue, sat, _picker.Value));
            return true;
        }
        // casillas
        for (int i = 0; i < ColorPicker.Slots; i++)
        {
            if (SlotRect(i, out int sx, out int sy, out int sw, out int sh) &&
                x >= sx && x < sx + sw && y >= sy && y < sy + sh)
            {
                _picker.ActiveSlot = i;
                var (h, s, v) = ColorPicker.ArgbToHsv(_picker.SlotColor[i]);
                _picker.Hue = h; _picker.Sat = s; _picker.Value = v;
                Invalidate();
                return true;
            }
        }
        return false;
    }

    private bool ValueSliderRect(out int x, out int y, out int w, out int h)
    {
        x = (int)(_wheelCx + _wheelR + 16);
        y = (int)(_wheelCy - _wheelR);
        w = 18;
        h = (int)(_wheelR * 2);
        return true;
    }

    private void ApplyPickerColor(int slot, int argb)
    {
        string hex = ColorPicker.ToHex(argb);
        switch (slot)
        {
            case 0: Config.Current.AccentColor = hex; break;
            case 1: Config.Current.BarFillColor = hex; break;
            case 2: Config.Current.BgColor = hex; break;
            case 3: Config.Current.StartBtnColor = hex; break;
        }
        _picker.SlotColor[slot] = argb;
        _renderer.ReloadBrushes(_hwnd, 0, 0);
        PersistConfig();
        Invalidate();
    }

    private bool SlotRect(int i, out int x, out int y, out int w, out int h)
    {
        int areaTop = WidgetsH;
        int areaBottom = WidgetsH + TreeH;
        int used = (int)(_wheelR * 2) + 40;   // rueda + margen
        int slotH = 22, gap = 6;
        int totalSlots = ColorPicker.Slots * slotH + (ColorPicker.Slots - 1) * gap;
        int sy0 = areaTop + used + 8;
        int sx = 20;
        w = FullWidthPx - 40;
        h = slotH;
        x = sx;
        y = sy0 + i * (slotH + gap);
        return y + h <= areaBottom - 4;
    }

    private void PaintPicker(RenderCtx ctx)
    {
        if (!_pickerOpen) return;
        // velo sobre el tree
        ctx.FillRect(Skin.Bg, 0, WidgetsH, FullWidthPx, TreeH);

        _wheelR = 72;
        _wheelCx = FullWidthPx / 2f;
        _wheelCy = WidgetsH + 40 + _wheelR;
        _picker.DrawWheel(ctx, _wheelCx, _wheelCy, _wheelR);
        // marcador movil del hue/sat elegido (posicion en la rueda)
        {
            double d = _picker.Sat * _wheelR;
            double ang = _picker.Hue * Math.PI * 2.0;
            float mx = _wheelCx + (float)(d * Math.Sin(ang));
            float my = _wheelCy - (float)(d * Math.Cos(ang));
            ctx.FillEllipse(Skin.White, mx, my, 5, 5);
            ctx.FillEllipse(Skin.Text, mx, my, 2.5f, 2.5f);
        }

        // slider de valor (brillo): vertical a la derecha, color pleno arriba -> negro abajo
        if (ValueSliderRect(out int vx, out int vy, out int vw, out int vh))
        {
            int full = ColorPicker.HsvToArgb(_picker.Hue, _picker.Sat, 1.0);
            int black = unchecked((int)0xFF000000);
            ctx.FillGradientV(full, black, vx, vy, vw, vh);
            ctx.Line(Skin.Divider, vx, vy, vx + vw, vy);
            ctx.Line(Skin.Divider, vx, vy + vh, vx + vw, vy + vh);
            // indicador del valor actual (arriba=1, abajo=0)
            float iy = vy + (1f - (float)_picker.Value) * (vh - 1);
            ctx.Line(Skin.White, vx - 2, iy, vx + vw + 2, iy, 2f);
        }

        // casillas
        for (int i = 0; i < ColorPicker.Slots; i++)
        {
            if (!SlotRect(i, out int sx, out int sy, out int sw, out int sh)) continue;
            bool active = i == _picker.ActiveSlot;
            ctx.FillRect(active ? Skin.Sel : Skin.Search, sx, sy, sw, sh);
            ctx.Line(Skin.Divider, sx, sy, sx + sw, sy);
            ctx.Line(Skin.Divider, sx, sy + sh, sx + sw, sy + sh);
            // swatch
            ctx.FillRect(_picker.SlotColor[i], sx + 4, sy + 4, 14, sh - 8);
            ctx.Text(ColorPicker.SlotNames[i], AppText.Fmt, Skin.Text, sx + 24, sy + 4, sw - 30, sh - 8);
        }
    }

    // serializa el config completo (AOT-safe, JSON a mano)
    private void PersistConfig()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var cfgDir = Path.Combine(dir, "ViennaBar");
            Directory.CreateDirectory(cfgDir);
            var cfgPath = Path.Combine(cfgDir, "config.json");
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"width\":").Append(Config.Current.Width)
              .Append(",\"revealMs\":").Append(Config.Current.RevealMs)
              .Append(",\"hideMs\":").Append(Config.Current.HideMs)
              .Append(",\"skin\":\"").Append(Config.Current.Skin).Append('"')
              .Append(",\"stayOpen\":").Append(Config.Current.StayOpen ? "true" : "false")
              .Append(",\"glassOverlay\":").Append(Config.Current.GlassOverlayEnabled ? "true" : "false");
            AppendHex(sb, "accent", Config.Current.AccentColor);
            AppendHex(sb, "barfill", Config.Current.BarFillColor);
            AppendHex(sb, "startbtn", Config.Current.StartBtnColor);
            AppendHex(sb, "bgoverride", Config.Current.BgColor);
            sb.Append(",\"Pins\":[");
            for (int i = 0; i < Config.Current.Pins.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Config.Current.Pins[i] ? "true" : "false");
            }
            sb.Append("]}");
            File.WriteAllText(cfgPath, sb.ToString());
        }
        catch (Exception ex) { AppLog($"persist config error: {ex.Message}"); }
    }

    private static void AppendHex(System.Text.StringBuilder sb, string key, string? hex)
    {
        sb.Append(",\"").Append(key).Append("\":");
        if (hex is null) sb.Append("null");
        else sb.Append('"').Append(hex).Append('"');
    }

    private void PaintScene()
    {
        if (_hidden) return;
        _renderer.DrawScene(ctx =>
        {
            // fondo con gradiente vertical sutil (SheenTop -> Bg) para evitar lo plano
            ctx.FillGradientV(Skin.SheenTop, Skin.Bg, 0, 0, FullWidthPx, ClientH);

            // accent bar izquierdo (identidad del tema): 2px en color de carpeta/acento
            ctx.FillRect(Skin.Accent, 0, 0, 2, ClientH);

            // divisores
            ctx.Line(Skin.Divider, 0, WidgetsH, FullWidthPx, WidgetsH);
            ctx.Line(Skin.Divider, 0, WidgetsH + TreeH, FullWidthPx, WidgetsH + TreeH);

            _widgets.Render(ctx, 0, 0, FullWidthPx, WidgetsH);

            _tree.Render(ctx, 0, WidgetsH, FullWidthPx, TreeH);
            _drawer.SetDrawerArea(DrawerH);
            _drawer.Render(ctx, 0, WidgetsH + TreeH, FullWidthPx, DrawerH, _drawerOpen);
            PaintDropMenu(ctx);
            PaintPicker(ctx);
        });
    }

    internal static int GET_X_LPARAM(LPARAM lp) => (int)(short)(lp.Value & 0xFFFF);
    internal static int GET_Y_LPARAM(LPARAM lp) => (int)(short)((lp.Value >> 16) & 0xFFFF);

    public void Dispose()
    {
        _ = UnregisterHotKey(_hwnd, HotKeyRotateSkin);
        _renderer.Dispose();
        _tree.Dispose();
        _drop?.Dispose();
        _drawer.Dispose();
    }
}
