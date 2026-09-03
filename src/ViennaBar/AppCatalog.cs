using System.Diagnostics;
using Shell = ViennaBar.ShellNative.ShellNative;

namespace ViennaBar;

// AppCatalog con cache persistente (hallazgo S4) â€” enum COM via vtables
// crudas del ViennaBar.Shell (AOT-total).
internal sealed unsafe class AppCatalog : IDisposable
{
    public sealed record AppEntry(string Name, string ParsingName, byte[] Pidl);

    private static readonly string[] JunkExtensions =
    {
        ".chm", ".hlp", ".txt", ".htm", ".html", ".pdf", ".rtf",
        ".doc", ".docx", ".nfo", ".ini", ".log",
    };

    private static bool IsLaunchable(string parsing)
    {
        if (parsing.StartsWith("::{") || !parsing.Contains('.')) return true;
        foreach (var ext in JunkExtensions)
            if (parsing.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private List<AppEntry> _apps = new();
    private string _cachePath = null!;
    private Thread? _refreshThread;

    public IReadOnlyList<AppEntry> Apps => _apps;
    private static void Log(string s) => Shell.DebugLog(s);

    public void Attach()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViennaBar");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "catalog.cache");

        var versionFile = Path.Combine(dir, "catalog.version");
        if (!File.Exists(versionFile) || File.ReadAllText(versionFile) != "2")
        {
            try
            {
                File.WriteAllText(versionFile, "2");
                if (File.Exists(_cachePath)) File.Delete(_cachePath);
            }
            catch { }
        }

        LoadCache();
        Log($"cache loaded: {_apps.Count} apps");

        try
        {
            Log($"shell debug: {Shell.DebugState()}");
        }
        catch (Exception ex) { Log($"debug FAIL: {ex.Message}"); }

        _refreshThread = new Thread(RefreshFromShell) { IsBackground = true };
        _refreshThread.Start();
    }

    private void LoadCache()
    {
        var list = ReadCache(_cachePath);
        if (list.Count > 0) _apps = list;
    }

    // formato binario del cache, estático para test headless (round-trip)
    internal static List<AppEntry> ReadCache(string path)
    {
        var empty = new List<AppEntry>();
        try
        {
            if (!File.Exists(path)) return empty;
            using var br = new BinaryReader(File.OpenRead(path));
            int n = br.ReadInt32();
            var list = new List<AppEntry>(n);
            for (int i = 0; i < n; i++)
            {
                string name = br.ReadString();
                string parsing = br.ReadString();
                int len = br.ReadInt32();
                byte[] pidl = len > 0 ? br.ReadBytes(len) : Array.Empty<byte>();
                list.Add(new AppEntry(name, parsing, pidl));
            }
            return list;
        }
        catch { return empty; }   // cache corrupta: se regenera
    }

    private void SaveCache() => WriteCache(_cachePath, _apps);

    internal static void WriteCache(string path, List<AppEntry> apps)
    {
        try
        {
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(apps.Count);
            foreach (var a in apps)
            {
                bw.Write(a.Name);
                bw.Write(a.ParsingName);
                bw.Write(a.Pidl.Length);
                if (a.Pidl.Length > 0) bw.Write(a.Pidl);
            }
        }
        catch { }
    }

    private void RefreshFromShell()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var list = new List<AppEntry>();

            var appsFolder = Shell.OpenAppsFolder();
            if (appsFolder == 0) return;
            try
            {
                foreach (var c in Shell.EnumChildren(appsFolder))
                {
                    if (c.Name.Length > 0 && c.ParsingName.Length > 0 && IsLaunchable(c.ParsingName))
                        list.Add(new AppEntry(c.Name, c.ParsingName, c.Pidl));
                }
            }
            finally { Shell.ReleaseFolder(appsFolder); }

            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            _apps = list;
            SaveCache();
            Log($"refresh background: {list.Count} apps en {sw.ElapsedMilliseconds} ms");
            App.Instance?.Invalidate();
        }
        catch (Exception ex)
        {
            Log($"refresh error: {ex.Message}");
        }
    }

    public IEnumerable<AppEntry> Search(string query) =>
        _apps.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _refreshThread?.Join(500);
}
