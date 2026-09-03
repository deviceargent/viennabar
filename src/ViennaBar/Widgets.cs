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
        _hwnd = hwnd;
        _running = true;
        _lastIdle = _lastKernel = _lastUser = 0;
        _ = SampleMetrics();   // baseline
        _ = SetTimer(hwnd, TimerTick, 1000, null);
        SampleDisks();
        try { _ = AddClipboardFormatListener(hwnd); } catch { }
    }

    public void OnTimer(HWND hwnd, nuint id)
    {
        if (id != TimerTick) return;
        _ = SampleMetrics();
        SampleDisks();
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
        by += 18;

        // ---- discos fijos: una fila compacta por unidad (label + barra uso) ----
        foreach (var d in _disks)
        {
            ctx.Text(d.label, AppText.Fmt, Skin.Muted, 12, by, 64, 14);
            DrawBar(ctx, 78, by + 2, w - 90, 10, d.usedFrac);
            by += 16;
        }

        // ---- portapapeles: ultimos textos (click = restaurar) ----
        _clipY0 = by;
        _clipN = 0;
        if (_clips.Count > 0)
        {
            ctx.Text($"Clip ({_clips.Count})", AppText.Fmt, Skin.Muted, 12, by, w - 20, 14);
            by += 15;
            int n = Math.Min(_clips.Count, 2);
            for (int i = 0; i < n; i++)
            {
                var t = _clips[i].Replace('\r', ' ').Replace('\n', ' ');
                if (t.Length > 38) t = t[..37] + "…";
                ctx.Text(t, AppText.Fmt, Skin.Text, 10, by, w - 24, 14);
                by += 15;
                _clipN++;
            }
        }

        // ---- Drop Stack panel: zona de drop visible (el CCW acepta toda la
        // ventana; este panel muestra el stack + estado del drag) ----
        by += 24;
        _stackPanelY = by;
        _stackPanelH = h - by - 4;
        _stackPanelW = w;
        float panelY = _stackPanelY, panelH = _stackPanelH;
        if (panelH < 40) return;

        // contenedor
        ctx.FillRect(_dragOver ? Skin.Sel : Skin.Search, 4, panelY, w - 8, panelH);
        ctx.Line(Skin.Divider, 4, panelY, w - 4, panelY);
        ctx.Line(Skin.Divider, 4, panelY + panelH, w - 4, panelY + panelH);
        ctx.Line(Skin.Divider, 4, panelY, 4, panelY + panelH);
        ctx.Line(Skin.Divider, w - 4, panelY, w - 4, panelY + panelH);

        // header
        string header = _dragOver ? "suelta para apilar" : $"Stack ({_dropStack?.Count ?? 0})";
        ctx.Text(header, AppText.FmtBig, _dragOver ? Skin.Text : Skin.Muted, 10, panelY + 4, w - 20, 16);

        // items (max los que entren)
        if (_dropStack is not null)
        {
            float iy = panelY + 22;
            int n = Math.Min(_dropStack.Count, (int)((panelH - 26) / 16f));
            int first = Math.Max(0, _dropStack.Count - n);   // ultimos N
            for (int i = _dropStack.Count - 1; i >= first && iy < panelY + panelH - 16; i--)
            {
                var name = _dropStack[i];
                int cut = name.LastIndexOf('\\');
                if (cut >= 0) name = name[(cut + 1)..];
                if (i == _pressedItem)
                    ctx.FillRect(Skin.Sel, 6, iy - 1, w - 12, 16);
                ctx.Text(name, AppText.Fmt, Skin.Text, 10, iy, w - 24, 14);
                iy += 16;
            }
        }
    }

    // wiring del drop (App lo conecta en MessageLoop)
    internal void SetDropStack(List<string> stack) => _dropStack = stack;
    internal void SetDragOver(bool over)
    {
        if (_dragOver != over)
        {
            _dragOver = over;
            App.Instance?.Invalidate();
        }
    }
    private List<string>? _dropStack;
    private bool _dragOver;

    // ---- discos fijos (widget 4): sample en cada tick, sin I/O de archivos ----
    private readonly List<(string label, double usedFrac)> _disks = new();

    internal unsafe void SampleDisks()
    {
        _disks.Clear();
        try
        {
            uint mask = GetLogicalDrives();
            for (int i = 0; i < 26 && _disks.Count < 4; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                string root = $"{(char)('A' + i)}:\\";
                uint dtype;
                fixed (char* p = root) dtype = GetDriveType(p);
                if (dtype != 3) continue;   // DRIVE_FIXED
                ulong free = 0, total = 0;
                fixed (char* p = root)
                {
                    ulong avail = 0, t = 0, f = 0;
                    if (!GetDiskFreeSpaceEx(root, &avail, &t, &f)) continue;
                    free = f; total = t;
                }
                if (total == 0) continue;
                double used = 1.0 - (double)free / total;
                _disks.Add(($"{root[0]}: {FormatGb(free)}", Math.Clamp(used, 0, 1)));
            }
        }
        catch { }
    }

    internal static string FormatGb(ulong bytes) => $"{bytes / 1073741824.0:0.#} GB";

    internal IReadOnlyList<(string label, double usedFrac)> Disks => _disks;

    // ---- portapapeles (widget 5): listener + historial de textos ----
    private readonly List<string> _clips = new();
    private HWND _hwnd;

    internal unsafe void OnClipboardUpdate()
    {
        try
        {
            if (!OpenClipboard(_hwnd)) return;
            try
            {
                var h = GetClipboardData(13);   // CF_UNICODETEXT
                if (h.IsNull) return;
                HGLOBAL hg = (HGLOBAL)h.Value.ToPointer();
                void* p = GlobalLock(hg);
                if (p is null) return;
                try
                {
                    var text = Marshal.PtrToStringUni((nint)p);
                    if (!string.IsNullOrWhiteSpace(text)) PushClip(text);
                }
                finally { _ = GlobalUnlock(hg); }
            }
            finally { _ = CloseClipboard(); }
        }
        catch { }
    }

    // historial con dedup (mueve al frente) y tope 5 — testeable headless
    internal void PushClip(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 200) text = text[..200];
        _clips.Remove(text);
        _clips.Insert(0, text);
        while (_clips.Count > 5) _clips.RemoveAt(_clips.Count - 1);
        App.Instance?.Invalidate();
    }

    internal IReadOnlyList<string> Clips => _clips;

    // click en una linea del clip -> restaura al portapapeles
    internal int ClipHitTest(int y)
    {
        for (int i = 0; i < _clipN; i++)
            if (y >= _clipY0 + 15 + i * 15 && y < _clipY0 + 30 + i * 15) return i;
        return -1;
    }

    internal unsafe void RestoreClip(int index)
    {
        if (index < 0 || index >= _clips.Count) return;
        var text = _clips[index];
        try
        {
            if (!OpenClipboard(_hwnd)) return;
            try
            {
                _ = EmptyClipboard();
                nuint bytes = (nuint)(text.Length + 1) * 2;
                var h = GlobalAlloc((Windows.Win32.System.Memory.GLOBAL_ALLOC_FLAGS)0x0002, bytes);   // GMEM_MOVEABLE
                if (h.Value is null) return;
                void* p = GlobalLock(h);
                if (p is null) { _ = GlobalFree(h); return; }
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, (nint)p, text.Length);
                    ((char*)p)[text.Length] = '\0';   // sin ZEROINIT: terminador manual
                }
                finally { _ = GlobalUnlock(h); }
                // el sistema toma posesion del HGLOBAL (no liberar tras Set)
                void* rawH = h;
                if (SetClipboardData(13, new HANDLE(new IntPtr(rawH))).IsNull) _ = GlobalFree(h);
            }
            finally { _ = CloseClipboard(); }
        }
        catch { }
    }

    private float _clipY0;
    private int _clipN;

    // ---- drag-out del stack: hit-test de los items visibles ----
    // Mismo layout que Render: ultimos N items visibles, 16px por item,
    // primer item (mas nuevo) ARRIBA del panel. Devuelve index en el stack
    // o -1. x,y en coordenadas del tercio widgets.
    internal int StackHitTest(int x, int y)
    {
        if (_dropStack is null || _dropStack.Count == 0 || _stackPanelH < 40) return -1;
        int n = Math.Min(_dropStack.Count, Math.Max(1, (int)((_stackPanelH - 26) / 16f)));
        int first = Math.Max(0, _dropStack.Count - n);
        float iy = _stackPanelY + 22;
        for (int i = _dropStack.Count - 1; i >= first; i--)
        {
            if (y >= iy && y < iy + 16) return i;
            iy += 16;
        }
        return -1;
    }

    // item resaltado durante el press (feedback), -1 = ninguno
    internal void SetPressedItem(int index)
    {
        if (_pressedItem != index)
        {
            _pressedItem = index;
            App.Instance?.Invalidate();
        }
    }

    // dimensiones del panel (actualizadas en cada Render; el hit-test ocurre
    // tras al menos un paint con la barra visible)
    private float _stackPanelY;
    private float _stackPanelH;
    private float _stackPanelW;
    private int _pressedItem = -1;

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
        try { _ = RemoveClipboardFormatListener(_hwnd); } catch { }
    }
}
