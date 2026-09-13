using System;

namespace ViennaBar;

// Rueda cromática HSV (disco hue/saturación) pre-renderizada a un bitmap BGRA
// premultiplicado. AOT-safe: se genera píxel a píxel una vez y se sube vía
// RenderCtx.UploadImage. El usuario elige hue (ángulo) + saturación (radio).
internal sealed class ColorPicker
{
    public const int Slots = 4;   // 0=listón, 1=barras, 2=fondo, 3=botón inicio
    public static readonly string[] SlotNames = { "Listón", "Barras", "Fondo", "Inicio" };

    private byte[]? _wheelBgra;
    private int _wheelN;
    private nint _wheelBmp;        // handle D2D (0 = re-subir en el próximo paint)
    private int _lastW, _lastH;

    public int ActiveSlot;
    public double Value = 1.0;   // brillo (0=negro, 1=color pleno). Se ajusta con el slider de valor.
    public double Hue, Sat;      // hue/saturación elegidos en la rueda (para el slider de valor)

    private const double WheelR = 46.0;   // radio del disco en px

    // color actual de cada slot (ARGB). Se inicializa desde los overrides/config.
    public int[] SlotColor = new int[Slots];

    public void SyncFromConfig()
    {
        SlotColor[0] = Skin.Accent;
        SlotColor[1] = Skin.BarFill;
        SlotColor[2] = Skin.Bg;
        SlotColor[3] = Skin.StartBtn;
    }

    // el RT murió (Resize/device-lost): el bitmap del disco queda dangling,
    // se re-subirá lazy en el próximo paint.
    public void OnRendererReset()
    {
        _wheelBmp = 0;
    }

    private byte[] BuildWheel(int size)
    {
        var bgra = new byte[size * size * 4];
        double cx = (size - 1) / 2.0, cy = (size - 1) / 2.0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double dx = x - cx, dy = y - cy;
                double r = Math.Sqrt(dx * dx + dy * dy);
                int o = (y * size + x) * 4;
                if (r > WheelR)
                {
                    bgra[o] = bgra[o + 1] = bgra[o + 2] = bgra[o + 3] = 0;   // transparente
                    continue;
                }
                double sat = Math.Min(1.0, r / WheelR);
                // hue: 0° en arriba (rojo), sentido horario
                double hue = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
                if (hue < 0) hue += 360.0;
                HsvToBgra(hue / 360.0, sat, 1.0, bgra, o);
            }
        return bgra;
    }

    private static void HsvToBgra(double h, double s, double v, byte[] dst, int o)
    {
        double r, g, b;
        int i = (int)(h * 6.0) % 6;
        double f = h * 6.0 - (int)(h * 6.0);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        switch (i)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        dst[o] = (byte)(b * 255.0);     // BGRA premultiplicado (alpha=255)
        dst[o + 1] = (byte)(g * 255.0);
        dst[o + 2] = (byte)(r * 255.0);
        dst[o + 3] = 255;
    }

    private nint EnsureWheel(RenderCtx ctx, int size)
    {
        if (_wheelBmp != 0 && _wheelN == size) return _wheelBmp;
        if (_wheelBgra is null || _wheelN != size)
        {
            _wheelBgra = BuildWheel(size);
            _wheelN = size;
            if (_wheelBmp != 0) { ctx.ReleaseImage(_wheelBmp); _wheelBmp = 0; }
        }
        if (_wheelBmp == 0)
            _wheelBmp = ctx.UploadImage(_wheelBgra, size, size);
        return _wheelBmp;
    }

    // dibuja el disco HSV centrado en (cx, cy) con radio r (ppx del disco = 2*WheelR escala)
    public void DrawWheel(RenderCtx ctx, float cx, float cy, float r)
    {
        float sizePx = r * 2;
        var bmp = EnsureWheel(ctx, 128);
        if (bmp == 0) return;
        ctx.DrawBitmap(bmp, cx - r, cy - r, sizePx, sizePx);
    }

    // hit-test del disco: dado un punto relativo al centro, devuelve hue/sat (o null fuera)
    public bool HitWheel(float cx, float cy, float r, float px, float py, out double hue, out double sat)
    {
        double dx = px - cx, dy = py - cy;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d > r) { hue = 0; sat = 0; return false; }
        sat = Math.Min(1.0, d / r);
        hue = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        if (hue < 0) hue += 360.0;
        hue /= 360.0;
        return true;
    }

    public static int HsvToArgb(double hue, double sat, double val = 1.0)
    {
        var tmp = new byte[4];
        HsvToBgra(hue, sat, val, tmp, 0);
        return unchecked((int)0xFF000000 | (tmp[2] << 16) | (tmp[1] << 8) | tmp[0]);
    }

    public static int ToArgb(string hex) => Skin.ParseHex(hex);
    public static string ToHex(int argb) =>
        $"#{((argb >> 16) & 0xFF):X2}{((argb >> 8) & 0xFF):X2}{(argb & 0xFF):X2}";

    // ARGB -> (h,s,v). h,s en [0,1], v en [0,1]. Usado para inicializar el picker
    // al cambiar de slot (el slider de valor refleja el color ya elegido).
    public static (double h, double s, double v) ArgbToHsv(int argb)
    {
        double r = ((argb >> 16) & 0xFF) / 255.0;
        double g = ((argb >> 8) & 0xFF) / 255.0;
        double b = (argb & 0xFF) / 255.0;
        double mx = Math.Max(r, Math.Max(g, b));
        double mn = Math.Min(r, Math.Min(g, b));
        double d = mx - mn;
        double h = 0;
        if (d != 0)
        {
            if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6.0;
        }
        double s = mx == 0 ? 0 : d / mx;
        return (h, s, mx);
    }
}