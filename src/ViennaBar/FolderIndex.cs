namespace ViennaBar;

// Indice de CARPETAS para busqueda instantanea (userland, sin MFT/admin).
// Alcance: PERFIL DEL USUARIO (madrigueras tipo Descargas/Documentos). Nada
// de system32/Windows/Program Files (navegables por arbol; el usuario lo pidio
// explicito). AppData cae solo por filtro hidden. Tipico: 5-20k dirs, <5MB,
// build <1s en background, search <1ms. Solo nombres+paths, sin contenido.
// Los junctions/reparse se saltean (loops tipo AppData\Local\Application Data).
internal sealed class FolderIndex
{
    public sealed record DirEntry(string Name, string FullPath);

    private List<DirEntry> _dirs = new();
    private readonly object _gate = new();
    private volatile bool _ready;
    private Thread? _builder;

    public bool Ready => _ready;
    public int Count { get { lock (_gate) return _dirs.Count; } }

    private const int MaxEntries = 100000;

    // raices personales (dedup por overlap/redirects tipo OneDrive). Testeable.
    internal static List<string> UserRoots()
    {
        var roots = new List<string>();
        void Add(string p)
        {
            try
            {
                if (string.IsNullOrEmpty(p) || !System.IO.Directory.Exists(p)) return;
                string f = System.IO.Path.GetFullPath(p).TrimEnd('\\');
                if (!roots.Contains(f, StringComparer.OrdinalIgnoreCase)) roots.Add(f);
            }
            catch { }
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        // Downloads posta (puede estar redirigido fuera del perfil, ej. OneDrive)
        try { Add(ShellTree.DownloadsPath()); } catch { }
        return roots;
    }

    public void StartBuild()
    {
        if (_builder is not null) return;
        _builder = new Thread(Build) { IsBackground = true };
        _builder.Start();
    }

    private void Build()
    {
        var list = new List<DirEntry>(20000);
        try
        {
            BuildFromRoots(UserRoots(), list);
        }
        catch { }
        lock (_gate) _dirs = list;
        _ready = true;
    }

    // walker sincrono sobre raices dadas (tests + build). No toca registry/COM.
    internal static void BuildFromRoots(IEnumerable<string> roots, List<DirEntry> list)
    {
        foreach (var root in roots)
        {
            if (list.Count >= MaxEntries) break;
            WalkRoot(root, list);
        }
    }

    private static void WalkRoot(string root, List<DirEntry> list)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && list.Count < MaxEntries)
        {
            string dir;
            try { dir = stack.Pop(); }
            catch { break; }
            string[] subs;
            try { subs = System.IO.Directory.GetDirectories(dir); }
            catch { continue; }   // acceso denegado / path largo / desaparecio
            foreach (var sub in subs)
            {
                System.IO.FileAttributes attr;
                try { attr = System.IO.File.GetAttributes(sub); }
                catch { continue; }
                const System.IO.FileAttributes skip =
                    System.IO.FileAttributes.Hidden |
                    System.IO.FileAttributes.System |
                    System.IO.FileAttributes.ReparsePoint;
                if ((attr & skip) != 0) continue;
                list.Add(new DirEntry(System.IO.Path.GetFileName(sub), sub));
                if (list.Count >= MaxEntries) break;
                stack.Push(sub);
            }
        }
    }

    // ranking: nombre exacto > empieza-con > contiene-nombre > contiene-path.
    // Empate: path mas corto primero. Tope 50. Insensible a acentos
    // (CompareInfo IgnoreNonSpace: "imagenes" matchea "Imágenes").
    public List<DirEntry> Search(string query)
    {
        List<DirEntry> snap;
        lock (_gate) snap = _dirs;
        var all = new List<DirEntry>(snap.Count + 8);
        all.AddRange(snap);
        all.AddRange(GetAliases());
        return SearchIn(all, query);
    }

    private static readonly System.Globalization.CompareInfo Cmp =
        System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
    private const System.Globalization.CompareOptions CmpOpt =
        System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;

    private static bool Eq(string a, string b) =>
        Cmp.Compare(a, b, CmpOpt) == 0;
    private static bool Starts(string a, string b) =>
        Cmp.IsPrefix(a, b, CmpOpt);
    private static bool Has(string a, string b) =>
        Cmp.IndexOf(a, b, CmpOpt) >= 0;

    // alias en español para las librerias personales (la barra habla español).
    // Se resuelven una vez por sesion (un redirect a mitad de sesion no pica).
    private static List<DirEntry>? _aliases;
    internal static List<DirEntry> GetAliases()
    {
        if (_aliases is not null) return _aliases;
        var list = new List<DirEntry>();
        void Add(string name, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path)) return;
                list.Add(new DirEntry(name, System.IO.Path.GetFullPath(path).TrimEnd('\\')));
            }
            catch { }
        }
        Add("Escritorio", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Descargas", ShellTree.DownloadsPath());
        Add("Imágenes", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Música", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        _aliases = list;
        return list;
    }

    internal static List<DirEntry> SearchIn(List<DirEntry> source, string query)
    {
        var out_ = new List<DirEntry>();
        if (string.IsNullOrWhiteSpace(query)) return out_;
        string q = query.Trim();
        var scored = new List<(int score, int len, DirEntry e)>(256);
        foreach (var d in source)
        {
            int s;
            if (Eq(d.Name, q)) s = 0;
            else if (Starts(d.Name, q)) s = 1;
            else if (Has(d.Name, q)) s = 2;
            else if (Has(d.FullPath, q)) s = 3;
            else continue;
            scored.Add((s, d.FullPath.Length, d));
        }
        scored.Sort((a, b) => a.score != b.score ? a.score - b.score : a.len - b.len);
        for (int i = 0; i < scored.Count && i < 50; i++) out_.Add(scored[i].e);
        return out_;
    }
}
