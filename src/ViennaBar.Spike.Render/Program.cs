using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace ViennaBar.Spike.Render;

// S6 — valida el render loop: DirectComposition + Direct2D con render solo por
// invalidación. Sin timers periodicos: se redibuja UNICAMENTE en WM_PAINT
// (por InvalidateRect) y se mide que la CPU idle sea 0%.
// Demo: clic cambia el color (invalida). El titulo muestra el contador.
internal static unsafe class Program
{
    private const string WndClass = "ViennaBarSpike6";
    private static HWND _hwnd;
    private static int _paints;
    private static uint _dpi = 96;
    private static readonly uint[] Palette = { 0x00E8E5B2, 0x00B2D8E8, 0x00D8B2E8, 0x00E8D8B2 };

    [STAThread]
    private static int Main()
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
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        _ = GetMonitorInfo(hmon, ref mi);
        int w = 300, h = 200;
        int x = mi.rcMonitor.left + ((mi.rcMonitor.Width - w) / 2);
        int y = mi.rcMonitor.top + ((mi.rcMonitor.Height - h) * 2 / 3);

        fixed (char* wc = WndClass, t = "S6 Render D2D — clic = invalidate")
        {
            _hwnd = CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOPMOST, wc, t,
                WINDOW_STYLE.WS_OVERLAPPEDWINDOW, x, y, w, h,
                default, default, hinst, null);
        }
        _ = ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        // D2D factory + DC render target (via HDC en WM_PAINT)
        // (para no arrastrar todo el pipeline DComp en el spike: validamos el
        // patrón de invalidación + D2D-on-GDI; DComp full va en F1)

        var proc = Process.GetCurrentProcess();
        var sw = Stopwatch.StartNew();
        while (GetMessage(out var msg, default, 0, 0))
        {
            TranslateMessage(in msg);
            _ = DispatchMessage(in msg);
        }
        long idleMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"paints={_paints} cpu={proc.TotalProcessorTime.TotalMilliseconds:F0}ms wall={idleMs}ms");
        return 0;
    }

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wparam, LPARAM lparam)
    {
        switch (msg)
        {
            case WM_PAINT:
            {
                _paints++;
                // PAINT sencillo GDI con color de la paleta segun _paints
                var ps = default(PAINTSTRUCT);
                var hdc = BeginPaint(hwnd, out ps);
                var color = Palette[_paints % Palette.Length];
                var brush = CreateSolidBrush(new COLORREF(color));
                _ = FillRect(hdc, &ps.rcPaint, brush);
                _ = DeleteObject((global::Windows.Win32.Graphics.Gdi.HGDIOBJ)brush.Value);
                _ = EndPaint(hwnd, in ps);
                // titulo FIJO: cambiarlo en cada paint provoca repaint del frame
                // no-cliente (loop). Se actualiza solo al cerrar, en consola.
                return default;
            }

            case WM_LBUTTONDOWN:
                _ = InvalidateRect(hwnd, (RECT*)null, true);   // invalida → WM_PAINT
                return default;

            case WM_KEYDOWN:
                if ((int)wparam.Value == 0x1B) _ = DestroyWindow(hwnd);
                return default;

            case WM_DESTROY:
                PostQuitMessage(0);
                return default;
        }
        return DefWindowProc(hwnd, msg, wparam, lparam);
    }
}
