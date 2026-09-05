using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;

namespace ViennaBar;

// UI engine (F2) â€” D2D + DWrite con vtables crudas (allowMarshaling=false,
// AOT-safe). Render por invalidaciÃ³n; brushes/formats cacheados una vez.
internal sealed unsafe class Renderer : IDisposable
{
    internal static void AppLog(string s) =>
        ShellNative.ShellNative.DebugLog(s);

    private ID2D1Factory* _factory;
    private ID2D1HwndRenderTarget* _rt;
    private IDWriteFactory* _dw;
    private IDWriteTextFormat* _text9;
    private IDWriteTextFormat* _text11b;

    private readonly Dictionary<string, nint> _brushes = new(); // name â†’ ID2D1SolidColorBrush*
    private uint _w, _h;

    public void Init()
    {
        AppLog("gfx: init begin");
        // D2D factory: void** â†’ struct vtable (AOT-safe, sin COM marshaling)
        Guid iidFactory = ID2D1Factory.IID_Guid;
        void* pFactory = null;
        var hr = ViennaBar.Gfx.GfxPInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED,
            in iidFactory, null, out pFactory);
        AppLog($"gfx: d2d factory hr=0x{(int)hr:X} ptr={(nint)pFactory != 0}");
        if (hr.Failed || pFactory is null) throw new InvalidOperationException($"D2D1CreateFactory hr=0x{(int)hr:X}");
        _factory = (ID2D1Factory*)pFactory;

        Guid iidDw = IDWriteFactory.IID_Guid;
        void* pDw = null;
        hr = ViennaBar.Gfx.GfxPInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, in iidDw, out pDw);
        AppLog($"gfx: dwrite factory hr=0x{(int)hr:X} ptr={(nint)pDw != 0}");
        if (hr.Failed || pDw is null) throw new InvalidOperationException($"DWriteCreateFactory hr=0x{(int)hr:X}");
        _dw = (IDWriteFactory*)pDw;

        AppLog("gfx: formats begin");
        _text9 = CreateFormat(9f);
        _text11b = CreateFormat(11f, bold: true);
        AppLog("gfx: init done");
    }

    private IDWriteTextFormat* CreateFormat(float size, bool bold = false)
    {
        fixed (char* pFont = "Segoe UI", pLocale = "es-ES")
        {
            IDWriteTextFormat* fmtRaw = null;
            try
            {
                _dw->CreateTextFormat(pFont, null,
                    bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
                    DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                    DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                    size, pLocale, &fmtRaw);
            }
            catch (Exception ex)
            {
                AppLog($"CreateTextFormat FAIL: {ex.Message}");
                throw;
            }
            if (fmtRaw is null) throw new InvalidOperationException("CreateTextFormat null");
            try
            {
                // una linea, truncation con ellipsis: los nombres largos del
                // tree NO deben derramarse sobre las filas vecinas
                fmtRaw->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
                try
                {
                    IDWriteInlineObject* sign = null;
                    _dw->CreateEllipsisTrimmingSign(fmtRaw, &sign);
                    var trim = new DWRITE_TRIMMING
                    {
                        granularity = DWRITE_TRIMMING_GRANULARITY.DWRITE_TRIMMING_GRANULARITY_CHARACTER,
                    };
                    fmtRaw->SetTrimming(&trim, sign);
                    // sign: 2 objetos en vida del proceso, costo cero (leak intencional)
                }
                catch { }
            }
            catch (Exception ex) { AppLog($"trim setup FAIL (no fatal): {ex.Message}"); }
            return fmtRaw;
        }
    }

    public void Resize(nint hwndRaw, uint width, uint height)
    {
        if (_rt is not null && width == _w && height == _h) return;
        if (_rt is null && (width < 4 || height < 4)) return; // sliver: nada

        HWND hwnd = new(hwndRaw);

        var prop = new D2D1_RENDER_TARGET_PROPERTIES
        {
            type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_DEFAULT,
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = global::Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
        };
        var hwndProp = new D2D1_HWND_RENDER_TARGET_PROPERTIES
        {
            hwnd = default,   // se setea abajo con el valor crudo (evita colisiÃ³n HWND core/Gfx)
            pixelSize = new D2D_SIZE_U { width = width, height = height },
            presentOptions = D2D1_PRESENT_OPTIONS.D2D1_PRESENT_OPTIONS_NONE,
        };
        *(nint*)&hwndProp.hwnd = hwndRaw;   // HWND es un nint de 8 bytes: asignaciÃ³n cruda

        ID2D1HwndRenderTarget* rtRaw = null;
        _factory->CreateHwndRenderTarget(&prop, &hwndProp, &rtRaw);
        if (rtRaw is null) return;

        if (_rt is not null) _ = ((ID2D1HwndRenderTarget*)_rt)->Release();
        _rt = (ID2D1HwndRenderTarget*)rtRaw;
        _w = width; _h = height;

        // los bitmaps mueren con el RT: purgar thumbs (se recargan lazy)
        PurgeThumbs();

        // recrear brushes (viven del RT)
        foreach (var p in _brushes.Values)
        {
            var b = (ID2D1SolidColorBrush*)p;
            _ = b->Release();
        }
        _brushes.Clear();
        foreach (var (name, argb) in Skin.Current.CacheBrushSpec)
            _brushes[name] = (nint)CreateBrush(argb);
    }

    private ID2D1SolidColorBrush* CreateBrush(int argb)
    {
        var c = ArgbToColorF(argb);
        ID2D1SolidColorBrush* b = null;
        _rt->CreateSolidColorBrush(&c, null, &b);
        return b;
    }

    private static D2D1_COLOR_F ArgbToColorF(int argb) => new()
    {
        r = ((argb >> 16) & 0xFF) / 255f,
        g = ((argb >> 8) & 0xFF) / 255f,
        b = (argb & 0xFF) / 255f,
        a = ((argb >> 24) & 0xFF) / 255f,
    };

    public void DrawScene(Action<RenderCtx> scene)
    {
        if (_rt is null) return;
        _rt->BeginDraw();
        var ctx = new RenderCtx(this);
        scene(ctx);
        // Si el device se perdio (TDR, sleep/wake, cambio de GPU), D2D pinta
        // negro para siempre: resetear todo lo que vive del RT. Lo proximo
        // se recrea lazy (Resize/brushes/thumbs).
        int hr = (int)_rt->EndDraw(null, null);
        if (hr == unchecked((int)0x8899000C))   // D2DERR_RECREATE_TARGET
        {
            AppLog("gfx: device lost, reseteando RT");
            PurgeThumbs();
            foreach (var p in _brushes.Values) _ = ((ID2D1SolidColorBrush*)p)->Release();
            _brushes.Clear();
            _ = ((ID2D1HwndRenderTarget*)_rt)->Release();
            _rt = null;
            _w = 0; _h = 0;
        }
    }

    internal ID2D1SolidColorBrush* BrushPtr(string name) =>
        _brushes.TryGetValue(name, out var p) ? (ID2D1SolidColorBrush*)p : null;

    internal IDWriteTextFormat* Text9 => _text9;
    internal IDWriteTextFormat* Text11b => _text11b;

    // ---- puentes vtable para RenderCtx (cast RT hwnd â†’ base) ----
    private static ID2D1RenderTarget* AsRt(ID2D1HwndRenderTarget* hwndRt) =>
        (ID2D1RenderTarget*)hwndRt;   // la vtable base estÃ¡ primero: cast directo

    internal void _rt_Clear(D2D1_COLOR_F* c) => AsRt(_rt)->Clear(c);

    internal void _rt_FillRect(D2D_RECT_F* r, ID2D1SolidColorBrush* b)
    {
        var brush = (Windows.Win32.Graphics.Direct2D.ID2D1Brush*)b;
        AsRt(_rt)->FillRectangle(r, brush);
    }

    internal void _rt_Line(D2D_POINT_2F p1, D2D_POINT_2F p2, ID2D1SolidColorBrush* b, float th)
    {
        var brush = (Windows.Win32.Graphics.Direct2D.ID2D1Brush*)b;
        AsRt(_rt)->DrawLine(p1, p2, brush, th, null);
    }

    internal void _rt_DrawText(char* s, uint len, IDWriteTextFormat* fmt, D2D_RECT_F* r, ID2D1SolidColorBrush* b)
    {
        var brush = (Windows.Win32.Graphics.Direct2D.ID2D1Brush*)b;
        AsRt(_rt)->DrawText(s, len, fmt, r, brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE,
            DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
    }

    internal void _rt_FillEllipse(float cx, float cy, float rx, float ry, ID2D1SolidColorBrush* b)
    {
        var e = new Windows.Win32.Graphics.Direct2D.D2D1_ELLIPSE
        {
            point = new D2D_POINT_2F { x = cx, y = cy },
            radiusX = rx,
            radiusY = ry,
        };
        var brush = (Windows.Win32.Graphics.Direct2D.ID2D1Brush*)b;
        AsRt(_rt)->FillEllipse(&e, brush);
    }

    internal void _rt_DrawBitmap(ID2D1Bitmap* bmp, D2D_RECT_F* dst)
    {
        AsRt(_rt)->DrawBitmap(bmp, dst, 1f,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, null);
    }

    // sube pixeles BGRA premultiplicados a un ID2D1Bitmap del RT actual.
    // 0 = fail. El caller libera con ReleaseBitmap (los bitmaps mueren con el RT).
    internal nint CreateBitmapFromPixels(int w, int h, nint pixels, uint pitch)
    {
        if (_rt is null || pixels == 0 || w <= 0 || h <= 0) return 0;
        var props = new D2D1_BITMAP_PROPERTIES
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = global::Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX = 96f,
            dpiY = 96f,
        };
        var size = new Windows.Win32.Graphics.Direct2D.Common.D2D_SIZE_U { width = (uint)w, height = (uint)h };
        ID2D1Bitmap* bmp = null;
        try
        {
            AsRt(_rt)->CreateBitmap(size, (void*)pixels, pitch, &props, &bmp);
        }
        catch { bmp = null; }
        return (nint)bmp;
    }

    internal void ReleaseBitmap(nint bmp)
    {
        if (bmp != 0) _ = ((ID2D1Bitmap*)bmp)->Release();
    }

    // ---- cache de thumbnails por path (los bitmaps viven del RT) ----
    private readonly Dictionary<string, nint> _thumbs = new();

    // bitmap del thumb (o 0): resuelve via Shell una vez y cachea.
    // size fijo 64: SIIG escala, D2D re-escala al dibujar.
    // Los FALLOS no se cachean: un SIIG transitorio (archivo en uso durante
    // un move) reintenta en el proximo paint en vez de brickearse.
    internal nint GetThumb(string path)
    {
        if (_thumbs.TryGetValue(path, out var hit)) return hit;
        nint bmp = 0;
        var px = ViennaBar.ShellNative.ShellNative.GetThumbnailPixels(path, 64);
        if (px is not null)
        {
            try { bmp = CreateBitmapFromPixels(px.Value.w, px.Value.h, px.Value.buf, (uint)(px.Value.w * 4)); }
            finally { ViennaBar.ShellNative.ShellNative.FreeThumbnail(px.Value.buf); }
            if (bmp != 0)
            {
                if (_thumbs.Count > 40) PurgeThumbs();
                _thumbs[path] = bmp;
            }
        }
        return bmp;
    }

    private void PurgeThumbs()
    {
        foreach (var p in _thumbs.Values) ReleaseBitmap(p);
        _thumbs.Clear();
    }

    public TextFormatHandle Text9Handle => new((nint)_text9);
    public TextFormatHandle Text11bHandle => new((nint)_text11b);
    public TextFormatHandle TextBigHandle => new((nint)_text11b); // reloj: 11b bold

    public void ReloadBrushes(HWND hwnd, uint width, uint height)
    {
        if (_rt is null) return;
        foreach (var p in _brushes.Values) _ = ((ID2D1SolidColorBrush*)p)->Release();
        _brushes.Clear();
        foreach (var (name, argb) in Skin.Current.CacheBrushSpec)
            _brushes[name] = (nint)CreateBrush(argb);
    }

    public void Dispose()
    {
        PurgeThumbs();
        foreach (var p in _brushes.Values) _ = ((ID2D1SolidColorBrush*)p)->Release();
        _brushes.Clear();
        if (_text9 is not null) { _ = ((IDWriteTextFormat*)_text9)->Release(); _text9 = null; }
        if (_text11b is not null) { _ = ((IDWriteTextFormat*)_text11b)->Release(); _text11b = null; }
        if (_rt is not null) { _ = ((ID2D1HwndRenderTarget*)_rt)->Release(); _rt = null; }
        if (_dw is not null) { _ = ((IDWriteFactory*)_dw)->Release(); _dw = null; }
        if (_factory is not null) { _ = ((ID2D1Factory*)_factory)->Release(); _factory = null; }
    }
}

// contexto de dibujo: aislamos los detalles vtable de los mÃ³dulos
internal unsafe struct RenderCtx
{
    private readonly Renderer _owner;

    internal RenderCtx(Renderer owner) => _owner = owner;

    public void Clear(int argb)
    {
        var c = ArgbToColorF(argb);
        _owner._rt_Clear(&c);
    }

    public void FillRect(int argb, float x, float y, float w, float h)
    {
        var r = new D2D_RECT_F { left = x, top = y, right = x + w, bottom = y + h };
        var b = _owner.BrushPtr(BrushName(argb));
        if (b is not null) _owner._rt_FillRect(&r, b);
    }

    public void Line(int argb, float x1, float y1, float x2, float y2, float thickness = 1f)
    {
        var p1 = new D2D_POINT_2F { x = x1, y = y1 };
        var p2 = new D2D_POINT_2F { x = x2, y = y2 };
        var b = _owner.BrushPtr(BrushName(argb));
        if (b is not null) _owner._rt_Line(p1, p2, b, thickness);
    }

    public unsafe void Text(string s, TextFormatHandle fmt, int argb, float x, float y, float maxW = 260f, float maxH = 18f)
    {
        var r = new D2D_RECT_F { left = x, top = y, right = x + maxW, bottom = y + maxH };
        var b = _owner.BrushPtr(BrushName(argb));
        fixed (char* p = s)
        {
            if (b is not null) _owner._rt_DrawText(p, (uint)s.Length, (IDWriteTextFormat*)fmt.Ptr, &r, b);
        }
    }

    public void FillEllipse(int argb, float cx, float cy, float rx, float ry)
    {
        var b = _owner.BrushPtr(BrushName(argb));
        if (b is not null) _owner._rt_FillEllipse(cx, cy, rx, ry, b);
    }

    public void DrawBitmap(nint bmp, float x, float y, float w, float h)
    {
        if (bmp == 0) return;
        var r = new D2D_RECT_F { left = x, top = y, right = x + w, bottom = y + h };
        unsafe { _owner._rt_DrawBitmap((ID2D1Bitmap*)bmp, &r); }
    }

    public nint GetThumb(string path) => _owner.GetThumb(path);

    // upload de pixeles BGRA premultiplicados gestionado por el caller
    // (thumbs del portapapeles): el buffer se pinnea solo durante el upload.
    public unsafe nint UploadImage(byte[] bgra, int w, int h)
    {
        if (bgra.Length < w * h * 4) return 0;
        fixed (byte* p = bgra)
            return _owner.CreateBitmapFromPixels(w, h, (nint)p, (uint)(w * 4));
    }

    public void ReleaseImage(nint bmp) => _owner.ReleaseBitmap(bmp);

    private static D2D1_COLOR_F ArgbToColorF(int argb) => new()
    {
        r = ((argb >> 16) & 0xFF) / 255f,
        g = ((argb >> 8) & 0xFF) / 255f,
        b = (argb & 0xFF) / 255f,
        a = ((argb >> 24) & 0xFF) / 255f,
    };

    // mapea argb â†’ brush cacheado del skin ACTUAL (tokens mutan en hot-reload)
    private static string BrushName(int argb)
    {
        foreach (var (name, value) in Skin.Current.CacheBrushSpec)
            if (value == argb) return name;
        return "text"; // fallback
    }
}

// handle opaco de text format para los mÃ³dulos (sin exponer vtables)
internal readonly struct TextFormatHandle(nint ptr)
{
    public nint Ptr { get; } = ptr;
}
