namespace ViennaBar;

internal static class AppText
{
    public static TextFormatHandle Fmt;
    public static TextFormatHandle FmtBig;
    public static void Init(Renderer r) { Fmt = r.Text9Handle; FmtBig = r.TextBigHandle; }
}
