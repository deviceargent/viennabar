namespace ViennaBar.Gfx;

using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;

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

    // PNG (y todo lo que WIC decodifique) -> BGRA premultiplicado top-down en
    // CoTaskMem. null = no decodificable (el caller usa placeholder).
    // Tope 1024px (un logo de 24px no justifica mas; evita allocs absurdos).
    public static (nint buf, int w, int h)? WicLoadPbgra32(string path)
    {
        IWICImagingFactory* factory = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICFormatConverter* conv = null;
        try
        {
            var hr = Windows.Win32.PInvoke.CoCreateInstance<IWICImagingFactory>(
                in Windows.Win32.PInvoke.CLSID_WICImagingFactory, null,
                CLSCTX.CLSCTX_INPROC_SERVER, out factory);
            if (hr.Failed || factory is null) return null;

            fixed (char* p = path)
            {
                try
                {
                    decoder = factory->CreateDecoderFromFilename(p, null,
                        (GENERIC_ACCESS_RIGHTS)0x80000000,
                        WICDecodeOptions.WICDecodeMetadataCacheOnDemand);
                }
                catch { return null; }
            }
            if (decoder is null) return null;

            try { decoder->GetFrame(0, &frame); }
            catch { return null; }
            if (frame is null) return null;

            uint w = 0, h = 0;
            try { frame->GetSize(&w, &h); }
            catch { return null; }
            if (w == 0 || h == 0 || w > 1024 || h > 1024) return null;

            try { factory->CreateFormatConverter(&conv); }
            catch { return null; }
            if (conv is null) return null;

            Guid dst = Windows.Win32.PInvoke.GUID_WICPixelFormat32bppPBGRA;
            try
            {
                conv->Initialize((IWICBitmapSource*)frame, &dst,
                    WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0.0,
                    WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
            }
            catch { return null; }

            nint mem = System.Runtime.InteropServices.Marshal.AllocCoTaskMem((int)(w * h * 4));
            try
            {
                conv->CopyPixels(null, w * 4, w * h * 4, (byte*)mem);
            }
            catch
            {
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(mem);
                return null;
            }
            return (mem, (int)w, (int)h);
        }
        finally
        {
            if (conv is not null) _ = conv->Release();
            if (frame is not null) _ = frame->Release();
            if (decoder is not null) _ = decoder->Release();
            if (factory is not null) _ = factory->Release();
        }
    }
}
