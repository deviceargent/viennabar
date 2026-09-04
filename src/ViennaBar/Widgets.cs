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

        // CPU / RAM barras minimal (% integrado en el label: sin linea
        // numerica aparte, que se leia como widget duplicado)
        float by = 40;
        ctx.Text($"CPU {_cpuPct:0}%", AppText.Fmt, Skin.Muted, 12, by, 64, 14);
        DrawBar(ctx, 78, by + 2, w - 90, 10, _cpuPct / 100.0);
        by += 20;
        ctx.Text($"RAM {_ramPct:0}%", AppText.Fmt, Skin.Muted, 12, by, 64, 14);
        DrawBar(ctx, 78, by + 2, w - 90, 10, _ramPct / 100.0);
        by += 20;

        // ---- discos fijos: una fila compacta por unidad (label + barra uso) ----
        foreach (var d in _disks)
        {
            ctx.Text(d.label, AppText.Fmt, Skin.Muted, 12, by, 64, 14);
            DrawBar(ctx, 78, by + 2, w - 90, 10, d.usedFrac);
            by += 16;
        }

        // ---- portapapeles: ultimos textos (click = restaurar) + tira de imagenes ----
        // header SIEMPRE (vacio = presente-sin-contenido, no desaparecido).
        // La fila marcada con ● es la que esta HOY en el portapapeles.
        _clipY0 = by;
        _clipN = 0;
        ctx.Text(_clips.Count > 0 ? $"Clip ({_clips.Count})" : "Clip", AppText.Fmt, Skin.Muted, 12, by, w - 20, 14);
        by += 15;
        if (_clips.Count > 0)
        {
            int n = Math.Min(_clips.Count, 3);
            for (int i = 0; i < n; i++)
            {
                var t = _clips[i].Replace('\r', ' ').Replace('\n', ' ');
                if (t.Length > 34) t = t[..33] + "…";
                bool marked = i == _copiedIdx && _copiedImg < 0;
                string mark = marked ? "● " : "  ";
                ctx.Text(mark + t, AppText.Fmt, marked ? Skin.Btn : Skin.Text, 10, by, w - 24, 14);
                by += 15;
                _clipN++;
            }
        }
        // tira de imagenes (32px, hasta 2): solo si entra en el tercio
        _imgY0 = by;
        _imgN = 0;
        while (_clipImages.Count > 2)
        {
            var old = _clipImages[0];
            _clipImages.RemoveAt(0);
            if (old.bmp != 0) ctx.ReleaseImage(old.bmp);
        }
        if (_clipImages.Count > 0 && h - by >= 50)
        {
            for (int i = 0; i < _clipImages.Count; i++)
            {
                var im = _clipImages[i];
                if (im.bmp == 0)
                {
                    var up = ctx.UploadImage(im.bgra, im.w, im.h);
                    if (up != 0) { im.bmp = up; _clipImages[i] = im; }
                }
                float ix = 12 + i * 40;
                if (im.bmp != 0) ctx.DrawBitmap(im.bmp, ix, by, 32, 32);
                else ctx.FillRect(Skin.Search, ix, by, 32, 32);
                if (i == _copiedImg)
                {
                    ctx.Line(Skin.Btn, ix - 1, by - 1, ix + 33, by - 1, 2f);
                    ctx.Line(Skin.Btn, ix - 1, by + 33, ix + 33, by + 33, 2f);
                }
                _imgN++;
            }
            by += 38;
        }

    }

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
            // el portapapeles es un lock global: si otro proceso lo tiene
            // abierto, reintentar (si no, ese contenido se pierde)
            bool opened = false;
            for (int i = 0; i < 3 && !(opened = OpenClipboard(_hwnd)); i++)
                Thread.Sleep(15);
            if (!opened) return;
            try
            {
                var h = GetClipboardData(13);   // CF_UNICODETEXT
                if (!h.IsNull)
                {
                    HGLOBAL hg = (HGLOBAL)h.Value.ToPointer();
                    void* p = GlobalLock(hg);
                    if (p is not null)
                    {
                        try
                        {
                            var text = Marshal.PtrToStringUni((nint)p);
                            if (!string.IsNullOrWhiteSpace(text)) PushClip(text);
                        }
                        finally { _ = GlobalUnlock(hg); }
                    }
                }
                // imagen (CF_DIB): va a la tira, y marca como copiada
                var hd = GetClipboardData(8);
                if (!hd.IsNull) PushClipImage(hd);
            }
            finally { _ = CloseClipboard(); }
        }
        catch { }
    }

    // captura DIB del handle ya lockeado por el caller (clipboard abierto).
    // Dedupea contra la ultima (evita doble entrada texto+imagen del mismo copy).
    private unsafe void PushClipImage(HANDLE hd)
    {
        try
        {
            HGLOBAL hg = (HGLOBAL)hd.Value.ToPointer();
            void* p = GlobalLock(hg);
            if (p is null) return;
            try
            {
                nuint size = GlobalSize(hg);
                if (size < 40 || size > (nuint)MaxClipImageBytes) return;
                var parsed = ParseDibToBgra(p, size);
                if (parsed is null) return;
                _clipImages.Insert(0, new ClipImage { bgra = parsed.Value.bgra, w = parsed.Value.w, h = parsed.Value.h });
                while (_clipImages.Count > MaxClipImages) _clipImages.RemoveAt(_clipImages.Count - 1);
                _copiedImg = 0;
                App.Instance?.Invalidate();
            }
            finally { _ = GlobalUnlock(hg); }
        }
        catch { }
    }

    // historial con dedup (mueve al frente) y tope 5 — testeable headless.
    // La entrada marcada (_copiedIdx) es la que esta en el portapapeles.
    internal void PushClip(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 200) text = text[..200];
        _clips.Remove(text);
        _clips.Insert(0, text);
        while (_clips.Count > 5) _clips.RemoveAt(_clips.Count - 1);
        _copiedIdx = 0;
        _copiedImg = -1;
        App.Instance?.Invalidate();
    }

    internal IReadOnlyList<string> Clips => _clips;
    internal int CopiedIndex => _copiedIdx;

    // click en una linea del clip -> restaura al portapapeles (y la marca)
    internal int ClipHitTest(int y)
    {
        for (int i = 0; i < _clipN; i++)
            if (y >= _clipY0 + 15 + i * 15 && y < _clipY0 + 30 + i * 15) return i;
        return -1;
    }

    internal void RestoreClip(int index)
    {
        if (index < 0 || index >= _clips.Count) return;
        if (WriteClipboardText(_clips[index])) { _copiedIdx = index; _copiedImg = -1; }
        App.Instance?.Invalidate();
    }

    // imagen bajo el punto -> index visible, o -1
    internal int ImgHitTest(int x, int y)
    {
        for (int i = 0; i < _imgN; i++)
        {
            float ix = 12 + i * 40;
            if (x >= ix && x < ix + 32 && y >= _imgY0 && y < _imgY0 + 32) return i;
        }
        return -1;
    }

    internal void RestoreClipImage(int index)
    {
        if (index < 0 || index >= _clipImages.Count) return;
        var im = _clipImages[index];
        if (WriteClipboardDib(im.bgra, im.w, im.h)) { _copiedImg = index; App.Instance?.Invalidate(); }
    }

    // el RT murio (Resize): los handles caen, los bytes quedan (re-upload lazy)
    internal void OnRendererReset()
    {
        for (int i = 0; i < _clipImages.Count; i++)
        {
            var im = _clipImages[i];
            im.bmp = 0;
            _clipImages[i] = im;
        }
    }

    private float _clipY0;
    private int _clipN;
    private float _imgY0;
    private int _imgN;
    private int _copiedIdx;
    private int _copiedImg = -1;   // -1 = ninguna (o un texto el marcado)

    internal struct ClipImage
    {
        public byte[] bgra;   // 32bpp premultiplicado top-down (para D2D y restore)
        public int w, h;
        public nint bmp;      // handle D2D (0 = subir en el proximo paint)
    }

    private readonly List<ClipImage> _clipImages = new();
    private const int MaxClipImages = 2;
    private const int MaxClipImageBytes = 8 * 1024 * 1024;

    // escribe texto al portapapeles. true = ok (el sistema toma el HGLOBAL).
    private unsafe bool WriteClipboardText(string text)
    {
        try
        {
            if (!OpenClipboard(_hwnd)) return false;
            try
            {
                _ = EmptyClipboard();
                nuint bytes = (nuint)(text.Length + 1) * 2;
                var h = GlobalAlloc((Windows.Win32.System.Memory.GLOBAL_ALLOC_FLAGS)0x0002, bytes);   // GMEM_MOVEABLE
                if (h.Value is null) return false;
                void* p = GlobalLock(h);
                if (p is null) { _ = GlobalFree(h); return false; }
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, (nint)p, text.Length);
                    ((char*)p)[text.Length] = '\0';   // sin ZEROINIT: terminador manual
                }
                finally { _ = GlobalUnlock(h); }
                // el sistema toma posesion del HGLOBAL (no liberar tras Set)
                void* rawH = h;
                if (SetClipboardData(13, new HANDLE(new IntPtr(rawH))).IsNull) { _ = GlobalFree(h); return false; }
                return true;
            }
            finally { _ = CloseClipboard(); }
        }
        catch { return false; }
    }

    // escribe imagen (bytes BGRA premult top-down) como CF_DIB. true = ok.
    private unsafe bool WriteClipboardDib(byte[] bgra, int w, int h)
    {
        try
        {
            if (bgra.Length < w * h * 4) return false;
            if (!OpenClipboard(_hwnd)) return false;
            try
            {
                _ = EmptyClipboard();
                int stride = w * 4;
                int total = 40 + stride * h;
                var hmem = GlobalAlloc((Windows.Win32.System.Memory.GLOBAL_ALLOC_FLAGS)0x0002, (nuint)total);
                if (hmem.Value is null) return false;
                void* p = GlobalLock(hmem);
                if (p is null) { _ = GlobalFree(hmem); return false; }
                try
                {
                    uint* hdr = (uint*)p;
                    hdr[0] = 40;                    // biSize
                    *(int*)(hdr + 1) = w;           // biWidth
                    *(int*)(hdr + 2) = h;           // biHeight>0 = bottom-up (canonico)
                    *(ushort*)(hdr + 3) = 1;              // biPlanes
                    *((ushort*)(hdr + 3) + 1) = 32;          // biBitCount
                    // resto en cero (BI_RGB)
                    byte* dst = (byte*)p + 40;
                    fixed (byte* src = bgra)
                    {
                        // des-premultiplica fila por fila invirtiendo orden
                        for (int y = 0; y < h; y++)
                        {
                            uint* s = (uint*)(src + (h - 1 - y) * stride);
                            uint* d = (uint*)(dst + y * stride);
                            for (int x = 0; x < w; x++)
                            {
                                uint px = s[x];
                                uint a = px >> 24;
                                if (a == 0 || a == 255) d[x] = px;
                                else d[x] = (a << 24)
                                    | ((((px >> 16) & 255) * 255 / a) << 16)
                                    | ((((px >> 8) & 255) * 255 / a) << 8)
                                    | (((px & 255) * 255 / a));
                            }
                        }
                    }
                }
                finally { _ = GlobalUnlock(hmem); }
                void* rawH = hmem;
                if (SetClipboardData(8, new HANDLE(new IntPtr(rawH))).IsNull) { _ = GlobalFree(hmem); return false; }
                return true;
            }
            finally { _ = CloseClipboard(); }
        }
        catch { return false; }
    }

    // CF_DIB (HGLOBAL con BITMAPINFOHEADER) -> BGRA premultiplicado top-down.
    // Solo 24/32bpp BI_RGB. null = no soportado (el caller prueba otra cosa).
    internal static unsafe (byte[] bgra, int w, int h)? ParseDibToBgra(void* mem, nuint size)
    {
        if (mem is null || size < 40) return null;
        uint* hdr = (uint*)mem;
        if (hdr[0] < 40) return null;
        int w = *(int*)(hdr + 1);
        int hh = *(int*)(hdr + 2);
        ushort planes = *(ushort*)(hdr + 3);
        ushort bpp = *((ushort*)(hdr + 3) + 1);
        uint comp = *(hdr + 4);
        if (w <= 0 || w > 4096 || planes != 1 || comp != 0) return null;
        if (bpp != 24 && bpp != 32) return null;
        int h = hh < 0 ? -hh : hh;
        if (h <= 0 || h > 4096) return null;
        int stride = ((w * bpp + 31) / 32) * 4;
        if ((nuint)(40 + stride * h) > size) return null;
        var bgra = new byte[w * h * 4];
        bool allZeroAlpha = true;
        fixed (byte* dst = bgra)
        {
            byte* src = (byte*)mem + 40;
            for (int y = 0; y < h; y++)
            {
                byte* srow = hh > 0 ? src + (h - 1 - y) * stride : src + y * stride;
                uint* d = (uint*)(dst + y * w * 4);
                if (bpp == 32)
                {
                    uint* s = (uint*)srow;
                    for (int x = 0; x < w; x++)
                    {
                        uint px = s[x];
                        if ((px >> 24) != 0) allZeroAlpha = false;
                        d[x] = px;
                    }
                }
                else
                {
                    for (int x = 0; x < w; x++)
                        d[x] = 0xFF000000u | ((uint)srow[x * 3 + 2] << 16) | ((uint)srow[x * 3 + 1] << 8) | srow[x * 3];
                }
            }
            if (bpp == 32 && allZeroAlpha)
            {
                uint* dd = (uint*)dst;
                for (int i = 0; i < w * h; i++) dd[i] |= 0xFF000000u;
            }
            // premultiplicar alfa real
            for (int i = 0; i < w * h; i++)
            {
                uint* dd = (uint*)dst;
                uint px = dd[i];
                uint a = px >> 24;
                if (a != 0 && a != 255)
                    dd[i] = (a << 24)
                        | ((((px >> 16) & 255) * a / 255) << 16)
                        | ((((px >> 8) & 255) * a / 255) << 8)
                        | (((px & 255) * a / 255));
            }
        }
        return (bgra, w, h);
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
        try { _ = RemoveClipboardFormatListener(_hwnd); } catch { }
    }
}
