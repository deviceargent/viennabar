using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using static Windows.Win32.PInvoke;

namespace ViennaBar;

// UI engine (F1) — D2D HwndRenderTarget + DWrite, render por invalidación.
// Cero objetos creados por frame: brushes/formats se cachean una vez.
// (DComp + animaciones del compositor: F2.)
internal sealed unsafe class Renderer : IDisposable
{
    private ID2D1Factory _factory = null!;
    private ID2D1HwndRenderTarget _rt = null!;
    private IDWriteFactory _dw = null!;
    private IDWriteTextFormat _text9 = null!;
    private IDWriteTextFormat _text11b = null!;

    private readonly Dictionary<int, ID2D1SolidColorBrush> _brushes = new();
    private ID2D1LinearGradientBrush? _sheen;
    private uint _w, _h;

    public void Init()
    {
        // factory + DWrite: SIN hwnd — se crea antes de la primera SetPos/Resize
        Guid iidFactory = typeof(ID2D1Factory).GUID;
        var hr = D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED,
            in iidFactory, null, out var factoryObj);
        if (hr.Failed) throw new InvalidOperationException($"D2D1CreateFactory hr=0x{(int)hr:X}");
        _factory = (ID2D1Factory)factoryObj;

        Guid iidDw = typeof(IDWriteFactory).GUID;
        _ = DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED,
            in iidDw, out var dwObj);
        _dw = (IDWriteFactory)dwObj;

        _text9 = CreateFormat(9f);
        _text11b = CreateFormat(11f, bold: true);
    }

    public void Attach(HWND hwnd, uint width, uint height)
    {
        Resize(hwnd, width, height);
    }

    private IDWriteTextFormat CreateFormat(float size, bool bold = false)
    {
        _dw.CreateTextFormat("Segoe UI", null,
            bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
            DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
            size, "es-ES", out var fmt);
        return fmt;
    }

    public void Resize(HWND hwnd, uint width, uint height)
    {
        if (_rt is not null && width == _w && height == _h) return;   // sin cambio real
        if (_rt is null && (width == 0 || height == 0)) return;        // sliver inicial: nada

        var old = _rt;
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
            hwnd = hwnd,
            pixelSize = new D2D_SIZE_U { width = width, height = height },
            presentOptions = D2D1_PRESENT_OPTIONS.D2D1_PRESENT_OPTIONS_NONE,
        };
        _factory.CreateHwndRenderTarget(in prop, in hwndProp, out _rt);
        _w = width; _h = height;
        _brushes.Clear();
        _sheen = null;
        if (old is not null) Marshal.ReleaseComObject(old);
        CreateBrushes();
    }

    private void CreateBrushes()
    {
        foreach (var (name, argb) in Skin.CacheBrushSpec)
            _brushes[name.GetHashCode()] = MakeBrush(argb);
    }

    private ID2D1SolidColorBrush MakeBrush(int argb)
    {
        _rt.CreateSolidColorBrush(ArgbToColorF(argb), null, out var b);
        return b;
    }

    private static D2D1_COLOR_F ArgbToColorF(int argb) => new()
    {
        r = ((argb >> 16) & 0xFF) / 255f,
        g = ((argb >> 8) & 0xFF) / 255f,
        b = (argb & 0xFF) / 255f,
        a = ((argb >> 24) & 0xFF) / 255f,
    };

    public ID2D1SolidColorBrush Brush(string name) =>
        _brushes.TryGetValue(name.GetHashCode(), out var b) ? b : throw new KeyNotFoundException(name);

    public IDWriteTextFormat Text9 => _text9;
    public IDWriteTextFormat Text11b => _text11b;

    public void DrawScene(Action<RenderCtx> scene)
    {
        _rt.BeginDraw();
        var ctx = new RenderCtx(this, _rt);
        scene(ctx);
        _ = _rt.EndDraw();
    }

    public void Dispose()
    {
        foreach (var b in _brushes.Values) Marshal.ReleaseComObject(b);
        if (_sheen is not null) Marshal.ReleaseComObject(_sheen);
        if (_rt is not null) Marshal.ReleaseComObject(_rt);
        if (_text9 is not null) Marshal.ReleaseComObject(_text9);
        if (_text11b is not null) Marshal.ReleaseComObject(_text11b);
        if (_dw is not null) Marshal.ReleaseComObject(_dw);
        if (_factory is not null) Marshal.ReleaseComObject(_factory);
    }
}

// contexto de dibujo que los módulos consumen (aisla los detalles D2D)
internal readonly struct RenderCtx
{
    private readonly Renderer _owner;
    private readonly ID2D1RenderTarget _rt;

    internal RenderCtx(Renderer owner, ID2D1RenderTarget rt) { _owner = owner; _rt = rt; }

    public void Clear(int argb) => _rt.Clear(ArgbToColorF(argb));

    public void FillRect(int argbBrush, float x, float y, float w, float h)
    {
        var r = new D2D_RECT_F { left = x, top = y, right = x + w, bottom = y + h };
        _rt.FillRectangle(in r, _owner.Brush(BrushName(argbBrush)));
    }

    public void Line(int argbBrush, float x1, float y1, float x2, float y2, float thickness = 1f)
    {
        var p1 = new global::Windows.Win32.Graphics.Direct2D.Common.D2D_POINT_2F { x = x1, y = y1 };
        var p2 = new global::Windows.Win32.Graphics.Direct2D.Common.D2D_POINT_2F { x = x2, y = y2 };
        _rt.DrawLine(p1, p2, _owner.Brush(BrushName(argbBrush)), thickness, null);
    }

    public unsafe void Text(string s, IDWriteTextFormat fmt, int argbBrush, float x, float y, float maxW = 260f, float maxH = 18f)
    {
        var r = new D2D_RECT_F { left = x, top = y, right = x + maxW, bottom = y + maxH };
        fixed (char* p = s)
        {
            _rt.DrawText(p, (uint)s.Length, fmt, &r, _owner.Brush(BrushName(argbBrush)),
                D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE,
                DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
        }
    }

    // mapea argb → nombre de brush cacheado (evita crear objetos por frame)
    private static string BrushName(int argb) => argb switch
    {
        Skin.Bg => "bg",
        Skin.Text => "text",
        Skin.Muted => "muted",
        Skin.Divider => "divider",
        Skin.Sel => "sel",
        Skin.Btn => "btn",
        Skin.White => "white",
        Skin.Search => "search",
        unchecked((int)0xFFC8E0EE) => "sheenTop",
        _ => "text",
    };

    private static D2D1_COLOR_F ArgbToColorF(int argb) => new()
    {
        r = ((argb >> 16) & 0xFF) / 255f,
        g = ((argb >> 8) & 0xFF) / 255f,
        b = (argb & 0xFF) / 255f,
        a = ((argb >> 24) & 0xFF) / 255f,
    };
}
