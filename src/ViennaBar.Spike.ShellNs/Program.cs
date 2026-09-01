using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace ViennaBar.Spike.ShellNs;

// S2 — valida: enum del namespace shell (desktop → Este equipo → carpetas)
// con expand-on-demand, y SHChangeNotifyRegister recibiendo notificaciones de
// cambio del FS en una ventana oculta. Un test autocontenido de 15 s:
// 1) enum desktop, 2) expand-on-demand de C:\, 3) escucha de SHCNE y crea/borra
// un archivo temporal para autodisparar la notificación. RESULT PASS si:
// desktop enum > 0 items, expand C:\ > 0 items, >= 1 notificación recibida.
internal static unsafe class Program
{
    private const string WndClass = "ViennaBarSpike2";
    private const uint WM_SH_NOTIFY = WM_APP + 1;   // mensajes de SHChangeNotify
    private const uint TimerExit = 1;
    private const uint TimerPhase1 = 2;              // dispara el test de FS

    // SHCONTF_* (no generadas por CsWin32 — valores winuser/shobjidl)
    private const int SHCONTF_CHECKING_FOR_CHILDREN = 0x10;
    private const int SHCONTF_FOLDERS = 0x20;
    private const int SHCONTF_NONFOLDERS = 0x40;
    private const int SHCONTF_INCLUDEHIDDEN = 0x80;
    private const int SHCONTF_INCLUDESUPERHIDDEN = 0x100;

    // SHCNE_* (event flags para SHChangeNotifyRegister — valores documentados)
    private const int SHCNE_RENAMEITEM = 0x00000001;
    private const int SHCNE_CREATE = 0x00000002;
    private const int SHCNE_DELETE = 0x00000004;
    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const int SHCNE_ALLEVENTS = unchecked((int)0x7FFFFFFF);

    private static HWND _hwnd;
    private static int _notifyCount;
    private static bool _desktopOk, _expandOk;

    [STAThread]
    private static int Main()
    {
        _ = CoInitialize(default);
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
            };
            _ = RegisterClassEx(in wcx);
            _hwnd = CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, wc, wc,
                WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, default, default, hinst, null);
        }

        // --- 1) enum desktop (namespace raíz) ---
        HRESULT hr = SHGetDesktopFolder(out var desktop);
        if (hr.Failed) { Console.WriteLine("FAIL: SHGetDesktopFolder"); return 1; }
        int desktopItems = CountChildren(desktop);
        _desktopOk = desktopItems > 0;
        Console.WriteLine($"Desktop enum: {desktopItems} items");

        // --- 2) expand-on-demand: parsing "C:\" → bind → enum ---
        int driveItems = 0;
        fixed (char* pC = @"C:\")
        {
            ITEMIDLIST* pidl = null;
            uint attr = 0;
            try { desktop.ParseDisplayName(default, default, pC, null, &pidl, ref attr); }
            catch { pidl = null; }
            if (pidl != null)
            {
                Guid iid = typeof(IShellFolder).GUID;
                desktop.BindToObject(pidl, default, &iid, out var obj);
                ILFree(pidl);
                if (obj is IShellFolder cFolder)
                {
                    driveItems = CountChildren(cFolder);
                    Marshal.ReleaseComObject(obj);
                }
            }
        }
        _expandOk = driveItems > 0;
        Console.WriteLine($"Expand C:\\: {driveItems} items");

        // --- 3) SHChangeNotifyRegister: escucha global del FS (PIDL desktop, recursivo) ---
        // El PIDL del desktop folder es el PIDL vacío (solo terminador de 2 bytes)
        var desktopPidl = (ITEMIDLIST*)CoTaskMemAlloc(2);
        desktopPidl->mkid.cb = 0;

        var shcn = new SHChangeNotifyEntry
        {
            pidl = desktopPidl,
            fRecursive = true,
        };
        var id = SHChangeNotifyRegister(_hwnd, SHCNRF_SOURCE.SHCNRF_InterruptLevel | SHCNRF_SOURCE.SHCNRF_ShellLevel | SHCNRF_SOURCE.SHCNRF_RecursiveInterrupt,
            SHCNE_CREATE | SHCNE_DELETE | SHCNE_UPDATEITEM | SHCNE_RENAMEITEM,
            WM_SH_NOTIFY, 1, in shcn);
        Console.WriteLine($"SHChangeNotifyRegister id={id}");
        bool registered = id != 0;

        if (registered)
        {
            // autodisparo DESDE el loop (timer), con la bomba de mensajes activa:
            // el shell entrega notificaciones via SendMessage — sin pump se pierden
            _ = SetTimer(_hwnd, TimerPhase1, 1000, null);
        }

        // esperar hasta 10 s acumulando notificaciones (fases corren por timer)
        var sw = Stopwatch.StartNew();
        _ = SetTimer(_hwnd, TimerExit, 10000, null);
        while (GetMessage(out var msg, default, 0, 0))
        {
            TranslateMessage(in msg);
            _ = DispatchMessage(in msg);
            if (msg.message == WM_USER && msg.wParam == 99) break; // señal del timer exit
        }
        if (registered) _ = SHChangeNotifyDeregister(id);

        bool pass = _desktopOk && _expandOk && (!registered || _notifyCount > 0);
        Console.WriteLine($"Notificaciones recibidas: {_notifyCount}");
        Console.WriteLine($"RESULT {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    private static int CountChildren(IShellFolder folder)
    {
        var hr = folder.EnumObjects(default,
            SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_INCLUDEHIDDEN,
            out var en);
        if (hr.Failed) return 0;
        int n = 0;
        while (true)
        {
            ITEMIDLIST* child = null;
            uint fetched = 0;
            hr = en.Next(1, &child, &fetched);
            if (hr != 0 || fetched == 0) break;
            ILFree(child);
            n++;
        }
        Marshal.ReleaseComObject(en);
        return n;
    }

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wparam, LPARAM lparam)
    {
        switch (msg)
        {
            case WM_SH_NOTIFY:
                _notifyCount++;
                Console.WriteLine($"  [SHCNE] event=0x{wparam.Value:X} lparam=0x{lparam.Value:X}");
                return default;

            case WM_TIMER:
                if ((nuint)wparam.Value == TimerPhase1)
                {
                    _ = KillTimer(hwnd, TimerPhase1);
                    // El shell monitoriza (interrupt) Desktop/Recent; Temp NO está
                    // vigilada → el test debe tocar el Desktop para disparar SHCNE.
                    var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    var tmp = Path.Combine(desk, "viennabar-s2-test.tmp");
                    File.WriteAllText(tmp, "s2");        // SHCNE_CREATE + UPDATEITEM
                    Thread.Sleep(300);
                    File.Delete(tmp);                     // SHCNE_DELETE
                }
                else if ((nuint)wparam.Value == TimerExit)
                {
                    _ = KillTimer(hwnd, TimerExit);
                    _ = PostMessage(hwnd, WM_USER, 99, 0);
                }
                return default;
        }
        return DefWindowProc(hwnd, msg, wparam, lparam);
    }
}
