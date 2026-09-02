namespace ViennaBar.Gfx;

// Puentes internos del assembly Gfx (resuelven su propio Windows.Win32.PInvoke
// generado con allowMarshaling=false). Visibles a ViennaBar via InternalsVisibleTo.
internal static unsafe class GfxPInvoke
{
    public static Windows.Win32.Foundation.HRESULT D2D1CreateFactory(
        Windows.Win32.Graphics.Direct2D.D2D1_FACTORY_TYPE t,
        in System.Guid riid,
        Windows.Win32.Graphics.Direct2D.D2D1_FACTORY_OPTIONS? opts,
        out void* pp)
        => Windows.Win32.PInvoke.D2D1CreateFactory(t, in riid, opts, out pp);

    public static Windows.Win32.Foundation.HRESULT DWriteCreateFactory(
        Windows.Win32.Graphics.DirectWrite.DWRITE_FACTORY_TYPE t,
        in System.Guid riid,
        out void* pp)
        => Windows.Win32.PInvoke.DWriteCreateFactory(t, in riid, out pp);
}
