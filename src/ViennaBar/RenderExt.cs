using Windows.Win32.Graphics.DirectWrite;

namespace ViennaBar;

internal static class AppText
{
    public static IDWriteTextFormat Fmt = null!;
    public static void Init(Renderer r) => Fmt = r.Text9;
}
