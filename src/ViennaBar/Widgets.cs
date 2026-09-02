using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemInformation;
using static Windows.Win32.PInvoke;

namespace ViennaBar;

// Widgets built-in (F2.2) — tercio superior. Todo P/Invoke puro: AOT-safe.
// Refresh: timer 1s SOLO cuando la barra está revelada (0 CPU cuando oculta).
internal sealed class Widgets : IDisposable
{
    private const uint TimerTick = 3;
    private bool _running;

    // métricas CPU (delta entre samples de GetSystemTimes)
    private long _lastIdle, _lastKernel, _lastUser;
    private double _cpuPct;
    private double _ramPct;
    private string _clock = "";

    public void Start(HWND hwnd)
    {
        _running = true;
        _lastIdle = _lastKernel = _lastUser = 0;
        _ = SampleMetrics();   // baseline
        _ = SetTimer(hwnd, TimerTick, 1000, null);
    }

    public void OnTimer(HWND hwnd, nuint id)
    {
        if (id != TimerTick) return;
        _ = SampleMetrics();
        App.Instance?.Invalidate();
    }

    private unsafe bool SampleMetrics()
    {
        // CPU: GetSystemTimes (idle, kernel, user) — delta entre llamadas
        System.Runtime.InteropServices.ComTypes.FILETIME idle, kernel, user;
        if (GetSystemTimes(&idle, &kernel, &user))
        {
            long idleD = FtToLong(idle) - _lastIdle;
            long totalD = (FtToLong(kernel) - _lastKernel) + (FtToLong(user) - _lastUser);
            if (totalD > 0)
                _cpuPct = Math.Clamp(100.0 * (totalD - idleD) / totalD, 0, 100);
            _lastIdle = FtToLong(idle); _lastKernel = FtToLong(kernel); _lastUser = FtToLong(user);
        }

        // RAM: GlobalMemoryStatusEx
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            _ramPct = mem.dwMemoryLoad;
        }

        // reloj
        GetLocalTime(out var st);
        _clock = $"{st.wHour:D2}:{st.wMinute:D2}";
        return true;
    }

    private static long FtToLong(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    public void Render(RenderCtx ctx, int x, int y, int w, int h)
    {
        if (!_running) return;

        // reloj grande (arriba)
        ctx.Text(_clock, AppText.FmtBig, Skin.Text, 12, 8, w - 20, 26);

        // CPU / RAM barras minimal
        float by = 40;
        ctx.Text("CPU", AppText.Fmt, Skin.Muted, 12, by, 30, 14);
        DrawBar(ctx, 46, by + 2, w - 58, 10, _cpuPct / 100.0);
        by += 20;
        ctx.Text("RAM", AppText.Fmt, Skin.Muted, 12, by, 30, 14);
        DrawBar(ctx, 46, by + 2, w - 58, 10, _ramPct / 100.0);

        // lectura numérica
        by += 22;
        ctx.Text($"CPU {_cpuPct:0}%   RAM {_ramPct:0}%", AppText.Fmt, Skin.Muted, 12, by, w - 20, 14);
    }

    private static void DrawBar(RenderCtx ctx, float x, float y, float w, float h, double frac)
    {
        // track
        ctx.FillRect(Skin.Search, x, y, w, h);
        // fill (clamp)
        frac = Math.Clamp(frac, 0, 1);
        if (frac > 0.01)
            ctx.FillRect(Skin.Btn, x, y, (float)(w * frac), h);
    }

    public void Dispose()
    {
        _running = false; // el timer lo mata el WM_DESTROY del host
    }
}
