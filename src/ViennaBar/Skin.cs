namespace ViennaBar;

// Theme engine — tokens del skin en ARGB int (D2D-friendly, sin System.Drawing).
// F2: override desde %APPDATA%\ViennaBar\skin.json + hot-reload.
internal sealed class Skin
{
    // nombres de brush cacheados por el Renderer
    public static readonly (string, int)[] CacheBrushSpec =
    {
        ("bg",       unchecked((int)0xFFE8F0F7)),
        ("sheenTop", unchecked((int)0xFFB0D8E8)),
        ("sheenBot", unchecked((int)0xFF78B8D8)),
        ("divider",  unchecked((int)0xFF5A9EC4)),
        ("text",     unchecked((int)0xFF1A3A50)),
        ("muted",    unchecked((int)0xFF4A6A80)),
        ("sel",      unchecked((int)0xFF9ED4EE)),
        ("btn",      unchecked((int)0xFF3A86C4)),
        ("btnHover", unchecked((int)0xFF2A76B4)),
        ("white",    unchecked((int)0xFFFFFFFF)),
        ("search",   unchecked((int)0xFFE6F5FC)),
    };

    public const int Bg = unchecked((int)0xFFE8F0F7);
    public const int Text = unchecked((int)0xFF1A3A50);
    public const int Muted = unchecked((int)0xFF4A6A80);
    public const int Divider = unchecked((int)0xFF5A9EC4);
    public const int Sel = unchecked((int)0xFF9ED4EE);
    public const int Btn = unchecked((int)0xFF3A86C4);
    public const int White = unchecked((int)0xFFFFFFFF);
    public const int Search = unchecked((int)0xFFE6F5FC);

    public static Skin LoadDefault() => new();
}
