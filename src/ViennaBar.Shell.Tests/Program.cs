using Shell = ViennaBar.ShellNative.ShellNative;
using ViennaBar.ShellNative;

namespace ViennaBarTests;

// Test AOT del pipeline de launch paso a paso � bisect del 0xC0000005.
// InternalsVisibleTo llega por transitivity? No: lo agregamos al Shell csproj.
internal static class Program
{
    private static int _failures;

    private static void Check(bool cond, string name)
    {
        Log($"{(cond ? "PASS" : "FAIL")}: {name}");
        if (!cond) _failures++;
    }
    private static void Log(string s)
    {
        try { Console.Out.Flush(); } catch { }
        Console.WriteLine(s);
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-tests.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); }
        catch { }
    }

    private static int Main(string[] args)
    {
        var stage = args.Length > 0 ? args[0] : "all";
        Log($"=== test begin stage={stage}");

        try
        {
            // stages headless puros (sin GUI, sin side-effects): red de
            // seguridad para refactors. `headless` los corre todos.
            if (stage == "dataobj") return StageDataObj();
            if (stage == "ccw") return StageCcw();
            if (stage == "skin") return StageSkin();
            if (stage == "cache") return StageCache();
            if (stage == "log") return StageLog();
            if (stage == "widgets") return StageWidgets();
            if (stage == "config") return StageConfig();
            if (stage == "fileop") return StageFileOp();
            if (stage == "m1") return StageM1();
            if (stage == "nav") return StageNav();
            if (stage == "launcher") return StageLauncher();
            if (stage == "m2ifeo") return StageM2Ifeo();
            if (stage == "findex") return StageFindEx();
            if (stage == "virtual") return StageVirtual();
            if (stage == "thumb") return StageThumb();
            if (stage == "prune") return StagePrune();
            if (stage == "snap") return StageSnap();
            if (stage == "headless") return StageHeadless();

            Log("stage1: OpenAppsFolder");
            var apps = Shell.OpenAppsFolder();
            Log($"stage1: apps={(apps != 0 ? "OK" : "FAIL")}");
            if (apps == 0) return 1;

            Log("stage2: EnumChildren");
            var children = Shell.EnumChildren(apps);
            Log($"stage2: {children.Count} children");
            if (children.Count == 0) return 2;

            // item seguro de lanzar: buscamos "Calculator" o el primero con .exe
            var target = children.FirstOrDefault(c => c.Name.Contains("Calculator", StringComparison.OrdinalIgnoreCase))
                ?? children.First(c => c.ParsingName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            Log($"stage3: target = {target?.Name} ({target?.ParsingName})");
            if (target is null) return 3;

            if (stage == "enum") { Log("=== enum-only done"); Shell.ReleaseFolder(apps); return 0; }

            // escalera bisect del hang de QueryContextMenu (app STA+ventana cuelga;
            // consola MTA pasa). Cada stage agrega un ingrediente:
            //   ctx2 = STA | ctx3 = STA+Ole | ctxdir = carpeta FS normal (no drive)
            if (stage == "ctx2" || stage == "ctx3" || stage == "ctxdir")
            {
                bool withWindow = stage == "ctx3";
                Log($"stage {stage}: begin");
                var done = new ManualResetEvent(false);
                int rc = 0;
                var t = new Thread(() =>
                {
                    try
                    {
                        if (stage == "ctx3") Shell.InitOleStaWithWindow();
                        else Shell.InitSta();
                        nint parent;
                        byte[] pidl;
                        if (stage == "ctxdir")
                        {
                            // carpeta FS normal: Documents -> primer child
                            var docs = Shell.OpenFolderByParsingName(
                                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                            Log($"stage: docs={(docs != 0 ? "OK" : "FAIL")}");
                            if (docs == 0) { rc = 31; return; }
                            var kids = Shell.EnumChildren(docs);
                            var folder = kids.FirstOrDefault(k => k.IsFolder && !k.ParsingName.StartsWith("::{"));
                            Log($"stage: target = {folder?.Name} ({folder?.ParsingName})");
                            parent = docs; pidl = folder!.Pidl;
                        }
                        else
                        {
                            var pc = Shell.OpenFolderByParsingName("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                            Log($"stage: pc={(pc != 0 ? "OK" : "FAIL")}");
                            if (pc == 0) { rc = 31; return; }
                            var kids = Shell.EnumChildren(pc);
                            var drive = kids.FirstOrDefault(k => k.IsFolder);
                            Log($"stage: target = {drive?.Name} ({drive?.ParsingName})");
                            parent = pc; pidl = drive!.Pidl;
                        }
                        Log("stage: QueryContextMenu...");
                        var res = Shell.DebugTestQueryContextMenu(parent, pidl);
                        Log($"stage: QueryContextMenu -> {res}");
                        Shell.ReleaseFolder(parent);
                    }
                    catch (Exception ex) { Log($"stage: EX {ex}"); rc = 99; }
                    finally { done.Set(); }
                });
                t.Start();
                if (!done.WaitOne(15000))
                {
                    Log($"stage {stage}: *** HANG (15s) ***");
                    return 33;
                }
                return rc;
            }

            if (stage == "ctxmenu")
            {
                // bisect del hang de QueryContextMenu del tree: mismo pipeline
                // (bind This PC -> drive child -> GetUIObjectOf -> QueryContextMenu)
                // PERO sin ventana/appbar. Si cuelga: handler/shell. Si pasa: app.
                Log("stageCtx: bind This PC");
                var pc = Shell.OpenFolderByParsingName("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                Log($"stageCtx: pc={(pc != 0 ? "OK" : "FAIL")}");
                if (pc == 0) return 31;
                try
                {
                    var kids = Shell.EnumChildren(pc);
                    Log($"stageCtx: {kids.Count} children de This PC");
                    var drive = kids.FirstOrDefault(k => k.IsFolder)
                        ?? kids.FirstOrDefault(k => k.ParsingName.StartsWith("::{") == false);
                    if (drive is null) { Log("stageCtx: sin child"); return 32; }
                    Log($"stageCtx: target = {drive.Name} ({drive.ParsingName})");
                    // QueryContextMenu directo (la API opaca ShowContextMenu hace
                    // TrackPopupMenu modal � no sirve para test; reproducimos el
                    // paso que cuelga con un menu descartable)
                    Log("stageCtx: ShowContextMenu (sin TrackPopupMenu real: se cuelga antes de eso segun log)");
                    System.Console.Out.Flush();
                    // NOTA: si el hang es en QueryContextMenu, este call no retorna.
                    // Usamos un hilo watchdog para reportarlo.
                    var done = new ManualResetEvent(false);
                    var t = new Thread(() =>
                    {
                        try
                        {
                            Log("stageCtx: DebugTestQueryContextMenu -> " + Shell.DebugTestQueryContextMenu(pc, drive.Pidl));
                        }
                        catch (Exception ex) { Log($"stageCtx: EX {ex.Message}"); }
                        finally { done.Set(); }
                    });
                    t.Start();
                    if (!done.WaitOne(15000))
                    {
                        Log("stageCtx: *** HANG confirmado en consola (15s) ***");
                        return 33;
                    }
                    return 0;
                }
                finally { Shell.ReleaseFolder(pc); }
            }

            if (stage == "cached")
            {
                // replica EXACTA del path del drawer: PIDLs desde catalog.cache
                var cachePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViennaBar", "catalog.cache");
                Log($"stageC: cache exists={System.IO.File.Exists(cachePath)}");
                if (!System.IO.File.Exists(cachePath)) return 21;
                using var br = new BinaryReader(System.IO.File.OpenRead(cachePath));
                int n = br.ReadInt32();
                Log($"stageC: {n} entries");
                var entries = new List<(string name, byte[] pidl)>();
                for (int i = 0; i < n; i++)
                {
                    string nm = br.ReadString();
                    string pr = br.ReadString();
                    int len = br.ReadInt32();
                    byte[] pidl = len > 0 ? br.ReadBytes(len) : Array.Empty<byte>();
                    entries.Add((nm, pidl));
                }
                var t = entries.FirstOrDefault(e => e.name.Contains("Calculator", StringComparison.OrdinalIgnoreCase));
                Log($"stageC: target={t.name} pidlLen={t.pidl?.Length ?? -1}");
                Shell.LaunchByPidl(apps, t.pidl);
                Log("stageC: launch by CACHED pidl returned sin crash");
                Shell.ReleaseFolder(apps);
                return 0;
            }

            // stage 4: pipeline LaunchByPidl completo
            Log("stage4: LaunchByPidl (esto lanza la app de verdad)");
            Shell.LaunchByPidl(apps, target.Pidl);
            Log("stage4: LaunchByPidl returned sin crash");

            Shell.ReleaseFolder(apps);
            Log("=== test done OK");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"EXCEPTION: {ex}");
            return 99;
        }
    }

    // =============== stages headless ===============

    private static int StageHeadless()
    {
        int rc = 0;
        if (StageEnumShell() != 0) rc = 1;
        if (StageDataObj() != 0) rc = 1;
        if (StageCcw() != 0) rc = 1;
        if (StageSkin() != 0) rc = 1;
        if (StageCache() != 0) rc = 1;
        if (StageLog() != 0) rc = 1;
        if (StageWidgets() != 0) rc = 1;
        if (StageConfig() != 0) rc = 1;
        if (StageFileOp() != 0) rc = 1;
        if (StageM1() != 0) rc = 1;
        if (StageNav() != 0) rc = 1;
        if (StageLauncher() != 0) rc = 1;
        if (StageM2Ifeo() != 0) rc = 1;
        if (StageFindEx() != 0) rc = 1;
        if (StageVirtual() != 0) rc = 1;
        if (StageThumb() != 0) rc = 1;
        if (StagePrune() != 0) rc = 1;
        if (StageSnap() != 0) rc = 1;
        Log(rc == 0 ? "=== headless ALL PASS" : "=== headless FAILURES");
        return rc;
    }

    private static int StageEnumShell()
    {
        _failures = 0;
        Log("-- enum-shell");
        var apps = Shell.OpenAppsFolder();
        Check(apps != 0, "enum: OpenAppsFolder != 0");
        if (apps != 0)
        {
            var kids = Shell.EnumChildren(apps);
            Check(kids.Count > 0, $"enum: children={kids.Count}");
            Shell.ReleaseFolder(apps);
        }
        return _failures == 0 ? 0 : 1;
    }

    private static unsafe int StageDataObj()
    {
        _failures = 0;
        Log("-- dataobj");
        var paths = new List<string>
        {
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-test fuego ��.txt"),
            @"C:\Windows\System32\notepad.exe",
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "con espacios", "a b.txt"),
        };
        nint dataObj = Shell.CreateDataObjectFromPaths(paths);
        Check(dataObj != 0, "dataobj: CreateDataObjectFromPaths != 0");
        if (dataObj != 0)
        {
            var back = Shell.PathsFromDataObject((void*)dataObj);
            Check(back.Count == paths.Count && back.SequenceEqual(paths),
                $"dataobj: round-trip {back.Count}/{paths.Count}");
            Shell.ReleaseDataObject(dataObj);
        }
        Check(Shell.CreateDataObjectFromPaths(new List<string>()) == 0, "dataobj: empty -> 0");
        return _failures == 0 ? 0 : 1;
    }

    private static unsafe int StageCcw()
    {
        _failures = 0;
        Log("-- ccw");
        // ctor Guid de 11 args (leccion F2.5: contar los literales)
        Guid iidSource = new(0x00000121, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);
        Guid iidTarget = new(0x00000122, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);
        Guid iidUnknown = new(0x00000000, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);

        using (var src = new DropSourceCcw())
        {
            Check(src.IUnknownPtr != 0, "ccw-src: IUnknownPtr != 0");
            void* com = (void*)src.IUnknownPtr;
            void** vt = *(void***)com;
            var qi = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vt[0];
            var addref = (delegate* unmanaged[Stdcall]<void*, int>)vt[1];
            var release = (delegate* unmanaged[Stdcall]<void*, int>)vt[2];
            void* pv = null;
            Check(qi(com, &iidSource, &pv) == 0 && pv == com, "ccw-src: QI IDropSource");
            Check(release(com) == 1, "ccw-src: Release tras QI -> 1");
            pv = null;
            Check(qi(com, &iidUnknown, &pv) == 0 && pv == com, "ccw-src: QI IUnknown");
            Check(release(com) == 1, "ccw-src: Release tras QI-unk -> 1");
            Guid bad = Guid.NewGuid();
            pv = (void*)1;
            Check(qi(com, &bad, &pv) != 0 && pv == null, "ccw-src: QI bad -> E_NOINTERFACE");
            Check(addref(com) == 2, "ccw-src: AddRef -> 2");
            Check(release(com) == 1, "ccw-src: Release -> 1");
            var qcd = (delegate* unmanaged[Stdcall]<void*, int, uint, int>)vt[3];
            var gf = (delegate* unmanaged[Stdcall]<void*, uint, int>)vt[4];
            Check(qcd(com, 1, 1) == unchecked((int)0x00040101), "ccw-src: ESC -> CANCEL");
            Check(qcd(com, 0, 0) == unchecked((int)0x00040100), "ccw-src: btn-up -> DROP");
            Check(qcd(com, 0, 1) == 0, "ccw-src: btn-down -> S_OK");
            Check(gf(com, 1) == unchecked((int)0x00040102), "ccw-src: GiveFeedback -> USEDEFAULTCURSORS");
        }

        using (var tgt = new DropTargetCcw())
        {
            Check(tgt.IUnknownPtr != 0, "ccw-tgt: IUnknownPtr != 0");
            void* com = (void*)tgt.IUnknownPtr;
            void** vt = *(void***)com;
            var qi = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vt[0];
            var release = (delegate* unmanaged[Stdcall]<void*, int>)vt[2];
            void* pv = null;
            Check(qi(com, &iidTarget, &pv) == 0 && pv == com, "ccw-tgt: QI IDropTarget");
            Check(release(com) == 1, "ccw-tgt: Release tras QI -> 1");
            var enter = (delegate* unmanaged[Stdcall]<void*, void*, uint, DropTargetCcw.PointL, uint*, int>)vt[3];
            var over = (delegate* unmanaged[Stdcall]<void*, uint, DropTargetCcw.PointL, uint*, int>)vt[4];
            var leave = (delegate* unmanaged[Stdcall]<void*, int>)vt[5];
            var drop = (delegate* unmanaged[Stdcall]<void*, void*, uint, DropTargetCcw.PointL, uint*, int>)vt[6];
            uint eff = 0;
            Check(enter(com, null, 0, new DropTargetCcw.PointL(10, 20), &eff) == 0 && eff == 1,
                "ccw-tgt: DragEnter -> COPY");
            eff = 0;
            Check(over(com, 0, new DropTargetCcw.PointL(10, 20), &eff) == 0 && eff == 1,
                "ccw-tgt: DragOver -> COPY");
            Check(leave(com) == 0, "ccw-tgt: DragLeave -> S_OK");
            eff = 0;
            Check(drop(com, null, 0, new DropTargetCcw.PointL(10, 20), &eff) == 0 && eff == 1,
                "ccw-tgt: Drop base -> COPY");
        }
        return _failures == 0 ? 0 : 1;
    }

    private static int StageSkin()
    {
        _failures = 0;
        Log("-- skin");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-skintest");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "skin.json");
        System.IO.File.WriteAllText(path,
            "{\"background\":\"#112233\",\"text\":\"#AABBCC\",\"muted\":\"#001122\"," +
            "\"divider\":\"#334455\",\"selection\":\"#556677\",\"button\":\"#778899\"," +
            "\"white\":\"#99AABB\",\"search\":\"#BBCCDD\",\"sheenTop\":\"#CCDDEE\"}");
        var skin = ViennaBar.Skin.LoadFromPath(path);
        var spec = skin.CacheBrushSpec.ToDictionary(t => t.Item1, t => t.Item2);
        Check(spec["bg"] == unchecked((int)0xFF112233), "skin: bg parse");
        Check(spec["text"] == unchecked((int)0xFFAABBCC), "skin: text parse");
        Check(spec["sheenTop"] == unchecked((int)0xFFCCDDEE), "skin: sheenTop parse");
        System.IO.File.WriteAllText(path, "{no es json");
        var broken = ViennaBar.Skin.LoadFromPath(path);
        var bspec = broken.CacheBrushSpec.ToDictionary(t => t.Item1, t => t.Item2);
        Check(bspec["bg"] == unchecked((int)0xFFC6D7E6), "skin: json roto -> defaults");
        System.IO.File.Delete(path);
        var missing = ViennaBar.Skin.LoadFromPath(path);
        var mspec = missing.CacheBrushSpec.ToDictionary(t => t.Item1, t => t.Item2);
        Check(mspec["bg"] == unchecked((int)0xFFC6D7E6), "skin: sin archivo -> defaults");
        // packaging: skins/<nombre>/ > legacy > ruta empaquetada (para crear)
        string appDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-skintest");
        string night = System.IO.Path.Combine(appDir, "skins", "noche", "skin.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(night)!);
        System.IO.File.WriteAllText(night, "{\"background\":\"#000001\"}");
        Check(ViennaBar.Skin.ResolveSkinPath("noche", appDir) == night, "skin: resuelve empaquetado");
        System.IO.File.Delete(night);
        Check(ViennaBar.Skin.ResolveSkinPath("noche", appDir) == night, "skin: sin legacy -> ruta empaquetada");
        System.IO.File.WriteAllText(path, "{\"background\":\"#000002\"}");
        Check(ViennaBar.Skin.ResolveSkinPath("noche", appDir) == path, "skin: fallback legacy");
        var viaLegacy = ViennaBar.Skin.LoadFromPath(ViennaBar.Skin.ResolveSkinPath("noche", appDir));
        Check(viaLegacy.CacheBrushSpec.ToDictionary(t => t.Item1, t => t.Item2)["bg"] == unchecked((int)0xFF000002),
            "skin: carga via fallback");
        System.IO.File.Delete(path);
        Check(ViennaBar.Skin.ResolveSkinPath("noche", appDir) == night, "skin: sin ninguno -> ruta empaquetada");
        // packaging: seed built-in + logo path + WIC graceful-null
        string packDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-skintest-pack");
        ViennaBar.Skin.SeedBuiltinSkins(packDir);
        string vn = System.IO.Path.Combine(packDir, "skins", "ViennaNight", "skin.json");
        Check(System.IO.File.Exists(vn), "skinpack: seed crea ViennaNight");
        var vspec = ViennaBar.Skin.LoadFromPath(vn).CacheBrushSpec.ToDictionary(t => t.Item1, t => t.Item2);
        Check(vspec["bg"] == unchecked((int)0xFF16202C), "skinpack: ViennaNight parse");
        System.IO.File.WriteAllText(vn, "{\"background\":\"#000099\"}");
        ViennaBar.Skin.SeedBuiltinSkins(packDir);
        Check(System.IO.File.ReadAllText(vn).Contains("000099"), "skinpack: seed no pisa existente");
        Check(ViennaBar.Skin.LogoPathFor(vn) is null, "skinpack: sin start.png -> null");
        string fakePng = System.IO.Path.Combine(packDir, "skins", "ViennaNight", "start.png");
        System.IO.File.WriteAllText(fakePng, "no es png");
        Check(ViennaBar.Skin.LogoPathFor(vn) == fakePng, "skinpack: logo path resuelve");
        Check(ViennaBar.Gfx.GfxPInvoke.WicLoadPbgra32(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-noexiste-xyz.png")) is null,
            "skinpack: WIC missing -> null");
        Check(ViennaBar.Gfx.GfxPInvoke.WicLoadPbgra32(fakePng) is null,
            "skinpack: WIC basura -> null");
        try { System.IO.Directory.Delete(packDir, true); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static int StageCache()
    {
        _failures = 0;
        Log("-- cache");
        var entries = new List<ViennaBar.AppCatalog.AppEntry>
        {
            new("Bloc de notas", "C:\\Windows\\notepad.exe", new byte[] { 1, 2, 3 }),
            new("�n�cod�", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", Array.Empty<byte>()),
            new("big", "C:\\x.exe", Enumerable.Range(0, 300).Select(i => (byte)(i & 255)).ToArray()),
        };
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-cachetest.bin");
        ViennaBar.AppCatalog.WriteCache(path, entries);
        var back = ViennaBar.AppCatalog.ReadCache(path);
        Check(back.Count == 3, $"cache: count={back.Count}");
        Check(back.Count == 3 && back[0].Name == "Bloc de notas" && back[0].ParsingName == "C:\\Windows\\notepad.exe",
            "cache: entry0 round-trip");
        Check(back.Count == 3 && back[1].Name == "�n�cod�" && back[1].Pidl.Length == 0,
            "cache: unicode + pidl vacia");
        Check(back.Count == 3 && back[2].Pidl.SequenceEqual(entries[2].Pidl),
            "cache: pidl 300B round-trip");
        Check(ViennaBar.AppCatalog.ReadCache(path + ".noexiste").Count == 0, "cache: missing -> empty");
        System.IO.File.WriteAllBytes(path, new byte[] { 9, 9, 9 });
        Check(ViennaBar.AppCatalog.ReadCache(path).Count == 0, "cache: corrupto -> empty");
        try { System.IO.File.Delete(path); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static int StageLog()
    {
        _failures = 0;
        Log("-- log");
        // el ring es global al proceso: medir lo previo para asserts deterministicos
        int before = Shell.DebugSnapshot().Length;
        Check(before < 256, $"log: ring previo={before} (<256)");
        string sentinel = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "viennabar-debug");
        Check(Shell.DebugEnabled == System.IO.File.Exists(sentinel), "log: DebugEnabled refleja sentinel");
        for (int i = 0; i < 300; i++) Shell.DebugLog($"tmark{i}");
        var snap = Shell.DebugSnapshot();
        Check(snap.Length == 256, $"log: snapshot len={snap.Length}");
        int first = 300 - 256;   // 300 markers > 256 slots: los 44 mas viejos
        Check(snap[255] != null && snap[255].EndsWith($" tmark299"), "log: ultimo = tmark299");   // (y todo lo previo evictado)
        Check(snap[0] != null && snap[0].EndsWith($" tmark{first}"), $"log: primero = tmark{first}");
        bool ordered = true;
        int prev = first - 1;
        foreach (var line in snap)
        {
            int p = line != null ? line.LastIndexOf("tmark") : -1;
            int n = p >= 0 && int.TryParse(line.Substring(p + 5), out int v) ? v : -1;
            if (n != prev + 1) { ordered = false; break; }
            prev = n;
        }
        Check(ordered, "log: ring ordenado y contiguo");
        return _failures == 0 ? 0 : 1;
    }

    private static int StageWidgets()
    {
        _failures = 0;
        Log("-- widgets");
        Check(ViennaBar.Widgets.FormatGb(0) == "0 GB", "widgets: 0B");
        Check(ViennaBar.Widgets.IsImagePath("a.PNG") && ViennaBar.Widgets.IsImagePath("b.jpeg")
            && !ViennaBar.Widgets.IsImagePath("c.txt") && !ViennaBar.Widgets.IsImagePath("sin-ext"),
            "widgets: IsImagePath");
        // layout CF_HDROP fabricado: header + paths + double-null
        var hb = ViennaBar.Widgets.BuildHDropBytes(new List<string> { "C:\\a b\\c.txt", "D:\\e.png" });
        bool hdropOk = hb[0] == 20 && hb[4] == 0 && hb[19] == 1;
        string all = System.Text.Encoding.Unicode.GetString(hb, 20, hb.Length - 22);
        hdropOk &= all == "C:\\a b\\c.txt\0D:\\e.png\0";
        hdropOk &= hb[hb.Length - 2] == 0 && hb[hb.Length - 1] == 0;
        Check(hdropOk, "widgets: BuildHDropBytes layout");        Check(ViennaBar.Widgets.FormatGb(1073741824) == "1 GB", "widgets: 1GiB");
        Check(ViennaBar.Widgets.FormatGb(123456789012) == "115 GB", "widgets: 115GiB");
        var w = new ViennaBar.Widgets();
        w.PushClip("  hola  ");
        w.PushClip("mundo");
        w.PushClip("hola");   // dedup: mueve al frente
        Check(w.Clips.Count == 2 && w.Clips[0] == "hola" && w.Clips[1] == "mundo", "widgets: push+dedup");
        w.PushClip("   ");
        Check(w.Clips.Count == 2, "widgets: vacio ignorado");
        for (int i = 0; i < 10; i++) w.PushClip($"c{i}");
        Check(w.Clips.Count == 5 && w.Clips[0] == "c9" && w.Clips[4] == "c5", "widgets: tope 5");
        w.SampleDisks();   // syscalls reales de solo lectura
        Check(w.Disks.Count > 0, $"widgets: discos={w.Disks.Count}");
        bool sane = true;
        foreach (var d in w.Disks) if (d.usedFrac < 0 || d.usedFrac > 1 || d.label.Length < 2) sane = false;
        Check(sane, "widgets: fracs en rango");
        // marcado de copiado
        var w2 = new ViennaBar.Widgets();
        w2.PushClip("uno");
        w2.PushClip("dos");
        Check(w2.CopiedIndex == 0 && w2.Clips[0] == "dos", "widgets: push marca frente");
        // DIB 24bpp 2x2 bottom-up -> BGRA premult top-down
        nint dib = System.Runtime.InteropServices.Marshal.AllocHGlobal(40 + 8 * 2);
        try
        {
            unsafe
            {
                byte* p = (byte*)(void*)dib;
                for (int i = 0; i < 56; i++) p[i] = 0;
                *(uint*)p = 40;
                *(int*)(p + 4) = 2; *(int*)(p + 8) = 2;
                *(ushort*)(p + 12) = 1; *(ushort*)(p + 14) = 24;
                // fila bottom: rojo, verde
                p[40] = 0; p[41] = 0; p[42] = 255; p[43] = 0; p[44] = 255; p[45] = 0;
                // fila top: azul, blanco
                p[48] = 255; p[49] = 0; p[50] = 0; p[51] = 255; p[52] = 255; p[53] = 255;
                var r = ViennaBar.Widgets.ParseDibToBgra((void*)dib, 56);
                Check(r is not null && r.Value.w == 2 && r.Value.h == 2, "widgets: dib24 dims");
                if (r is not null)
                {
                    uint[] px = new uint[4];
                    System.Buffer.BlockCopy(r.Value.bgra, 0, px, 0, 16);
                    Check(px[0] == 0xFF0000FFu && px[1] == 0xFFFFFFFFu
                        && px[2] == 0xFFFF0000u && px[3] == 0xFF00FF00u, "widgets: dib24 pixeles+flip");
                }
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(dib); }
        // DIB 32bpp con alfa real -> premultiplica; alfa 0 global -> opaco
        nint d32 = System.Runtime.InteropServices.Marshal.AllocHGlobal(48);
        try
        {
            unsafe
            {
                byte* p = (byte*)(void*)d32;
                for (int i = 0; i < 48; i++) p[i] = 0;
                *(uint*)p = 40;
                *(int*)(p + 4) = 2; *(int*)(p + 8) = 1;
                *(ushort*)(p + 12) = 1; *(ushort*)(p + 14) = 32;
                ((uint*)(p + 40))[0] = 0x80FF0000u;   // a=128 r=255
                ((uint*)(p + 40))[1] = 0x00000000u;
                var r = ViennaBar.Widgets.ParseDibToBgra((void*)d32, 48);
                bool ok = r is not null;
                uint[] px = new uint[2];
                if (ok) System.Buffer.BlockCopy(r.Value.bgra, 0, px, 0, 8);
                Check(ok && px[0] == 0x80800000u && px[1] == 0x00000000u, "widgets: dib32 premult");
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(d32); }
        nint bad = System.Runtime.InteropServices.Marshal.AllocHGlobal(64);
        try
        {
            unsafe
            {
                byte* p = (byte*)(void*)bad;
                for (int i = 0; i < 64; i++) p[i] = 0;
                *(uint*)p = 40;
                *(int*)(p + 4) = 2; *(int*)(p + 8) = 2;
                *(ushort*)(p + 12) = 1; *(ushort*)(p + 14) = 8;   // 8bpp no soportado
                Check(ViennaBar.Widgets.ParseDibToBgra((void*)bad, 64) is null, "widgets: dib8 -> null");
                Check(ViennaBar.Widgets.ParseDibToBgra((void*)bad, 10) is null, "widgets: dib corto -> null");
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(bad); }
        w.Dispose();
        return _failures == 0 ? 0 : 1;
    }

    private static int StageConfig()
    {
        _failures = 0;
        Log("-- config");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-configtest");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "config.json");
        System.IO.File.WriteAllText(path,
            "{\"width\":320,\"revealMs\":50,\"hideMs\":500,\"skin\":\"noche\"}");
        var c = ViennaBar.Config.LoadFromPath(path);
        Check(c.Width == 320 && c.RevealMs == 50 && c.HideMs == 500 && c.Skin == "noche", "config: parse");
        System.IO.File.WriteAllText(path, "{\"width\":9999,\"revealMs\":-5,\"skin\":\"..\\\\evil\"}");
        var clamped = ViennaBar.Config.LoadFromPath(path);
        Check(clamped.Width == 600 && clamped.RevealMs == 0 && clamped.Skin == "evil", "config: clamp+sanitize");
        System.IO.File.WriteAllText(path, "{roto");
        var broken = ViennaBar.Config.LoadFromPath(path);
        Check(broken.Width == 280 && broken.Skin == "default", "config: roto -> defaults");
        System.IO.File.Delete(path);
        var missing = ViennaBar.Config.LoadFromPath(path);
        Check(missing.Width == 280 && missing.HideMs == 400, "config: sin archivo -> defaults");
        return _failures == 0 ? 0 : 1;
    }

    private static int StageFileOp()
    {
        _failures = 0;
        Log("-- fileop");
        const ushort silent = (ushort)(ViennaBar.ShellNative.ShellNative.FOF_SILENT
            | ViennaBar.ShellNative.ShellNative.FOF_NOCONFIRMATION
            | ViennaBar.ShellNative.ShellNative.FOF_NOERRORUI
            | ViennaBar.ShellNative.ShellNative.FOF_NOCONFIRMMKDIR);
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-fileoptest");
        string src = System.IO.Path.Combine(root, "src");
        string dst = System.IO.Path.Combine(root, "dst");
        try { System.IO.Directory.Delete(root, true); } catch { }
        System.IO.Directory.CreateDirectory(src);
        System.IO.Directory.CreateDirectory(dst);
        string a = System.IO.Path.Combine(src, "a.txt");
        string b = System.IO.Path.Combine(src, "b con espacios.txt");
        System.IO.File.WriteAllText(a, "a");
        System.IO.File.WriteAllText(b, "b");
        // copiar 2 archivos de una vez (buffer multi-string) sin UI
        int rc = Shell.FileOperation(0, Shell.FO_COPY, new List<string> { a, b }, dst, silent);
        Check(rc == 0, $"fileop: copy rc=0x{rc:X}");
        Check(System.IO.File.Exists(System.IO.Path.Combine(dst, "a.txt"))
            && System.IO.File.Exists(System.IO.Path.Combine(dst, "b con espacios.txt"))
            && System.IO.File.Exists(a), "fileop: copia existe en ambos");
        // mover: sale del origen
        string dst2 = System.IO.Path.Combine(root, "dst2");
        System.IO.Directory.CreateDirectory(dst2);
        rc = Shell.FileOperation(0, Shell.FO_MOVE,
            new List<string> { System.IO.Path.Combine(dst, "a.txt") }, dst2, silent);
        Check(rc == 0, $"fileop: move rc=0x{rc:X}");
        Check(System.IO.File.Exists(System.IO.Path.Combine(dst2, "a.txt"))
            && !System.IO.File.Exists(System.IO.Path.Combine(dst, "a.txt")), "fileop: move reubica");
        Check(Shell.FileOperation(0, Shell.FO_COPY, new List<string>(), dst, silent) != 0,
            "fileop: vacio -> error");
        try { System.IO.Directory.Delete(root, true); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static int StageM1()
    {
        _failures = 0;
        Log("-- m1 (sandbox HKCU, se limpia al final)");
        string sb = @"Software\ViennaBarTests_M1";
        string classes = sb + @"\Classes";
        string backup = sb + @"\Backup";
        var hive = Microsoft.Win32.Registry.CurrentUser;
        try { hive.DeleteSubKeyTree(sb, false); } catch { }
        // seed: Directory con valores previos, Drive/Folder inexistentes
        using (var k = hive.CreateSubKey(classes + @"\Directory\shell\open\command"))
        {
            k?.SetValue("", "old-cmd");
            k?.SetValue("DelegateExecute", "{OLD}");
        }
        using (var k = hive.CreateSubKey(classes + @"\Directory\shell"))
        {
            k?.SetValue("", "none");   // default original estilo Win11
        }
        string regPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-m1test.reg");
        string summary = ViennaBar.Integration.ApplyM1(hive, classes, backup,
            @"C:\fake\ViennaBar.exe", regPath);
        Check(summary.Contains("M1 aplicado"), "m1: apply resumen");
        using (var k = hive.OpenSubKey(classes + @"\Directory\shell\open\command", false))
            Check(k is not null
                && (k.GetValue("") as string) == "\"C:\\fake\\ViennaBar.exe\" --open-folder \"%V\""
                && k.GetValue("DelegateExecute") is null, "m1: Directory override + DE borrado");
        using (var k = hive.OpenSubKey(classes + @"\Drive\shell\open\command", false))
            Check(k is not null && ((k.GetValue("") as string) ?? "").Contains("--open-folder"),
                "m1: Drive creado");
        using (var k = hive.OpenSubKey(backup + @"\Directory", false))
            Check(k is not null && (k.GetValue("Existed") as int?) == 1
                && (k.GetValue("Command") as string) == "old-cmd"
                && (k.GetValue("DelegateExecute") as string) == "{OLD}"
                && (k.GetValue("ShellDefault") as string) == "none", "m1: backup guarda previo");
        using (var k = hive.OpenSubKey(classes + @"\Directory\shell", false))
            Check(k is not null && (k.GetValue("") as string) == "open", "m1: shell default -> open");
        string reg = System.IO.File.ReadAllText(regPath);
        Check(reg.Contains("Windows Registry Editor Version 5.00")
            && reg.Contains("@=\"old-cmd\"") && reg.Contains("\"DelegateExecute\"=\"{OLD}\"")
            && reg.Contains("[-HKEY_CURRENT_USER\\" + classes + "\\Drive\\shell\\open\\command]"),
            "m1: rescue .reg");
        // write-once: segundo apply actualiza el override pero conserva el backup original
        string summary2 = ViennaBar.Integration.ApplyM1(hive, classes, backup, @"C:\otro\Bar.exe", regPath);
        Check(summary2.Contains("conservado"), "m1: segundo apply conserva backup");
        using (var k = hive.OpenSubKey(backup + @"\Directory", false))
            Check(k is not null && (k.GetValue("Command") as string) == "old-cmd", "m1: backup write-once");
        using (var k = hive.OpenSubKey(classes + @"\Directory\shell\open\command", false))
            Check(k is not null && ((k.GetValue("") as string) ?? "").Contains(@"C:\otro\Bar.exe"),
                "m1: re-apply actualiza command");
        string back = ViennaBar.Integration.RevertM1(hive, classes, backup);
        Check(back.Contains("M1 revertido"), "m1: revert resumen");
        using (var k = hive.OpenSubKey(classes + @"\Directory\shell\open\command", false))
            Check(k is not null && (k.GetValue("") as string) == "old-cmd"
                && (k.GetValue("DelegateExecute") as string) == "{OLD}", "m1: Directory restaurado");
        using (var k = hive.OpenSubKey(classes + @"\Directory\shell", false))
            Check(k is not null && (k.GetValue("") as string) == "none", "m1: shell default restaurado");
        using (var k = hive.OpenSubKey(classes + @"\Drive\shell\open\command", false))
            Check(k is null, "m1: Drive creado se borra");
        Check(hive.OpenSubKey(backup, false) is null, "m1: backup se borra");
        Check(ViennaBar.Integration.RevertM1(hive, classes, backup).Contains("nada que revertir"),
            "m1: revert sin backup");
        try { hive.DeleteSubKeyTree(sb, false); } catch { }
        try { System.IO.File.Delete(regPath); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static int StageNav()
    {
        _failures = 0;
        Log("-- nav");
        var tree = new ViennaBar.ShellTree();
        tree.Attach(default);
        string sub = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-navtest", "sub");
        System.IO.Directory.CreateDirectory(sub);
        tree.ExpandToPath(sub);
        Check(FindNode(tree.Roots, sub) is not null, "nav: nodo destino en el tree");
        // descenso desde raiz FS (Descargas/Escritorio): 2 niveles bajo la raiz
        string deep = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "vb-navtest", "sub");
        System.IO.Directory.CreateDirectory(deep);
        tree.ExpandToPath(deep);
        var deepNode = FindNode(tree.Roots, deep);
        Check(deepNode is not null && deepNode.Expanded, "nav: desciende 2 niveles desde raiz FS");
        Check(tree.VisibleCount >= 6, $"nav: visibles incluye nietos ({tree.VisibleCount})");
        Check(tree.Selected is not null && tree.Selected.ParsingName.TrimEnd('\\')
            .Equals(deep.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "nav: select destino");
        tree.ScrollBy(9999, 360);
        Check(tree.TopRow >= 0, "nav: scroll clamp alto");
        tree.ScrollBy(-9999, 360);
        Check(tree.TopRow == 0, "nav: scroll clamp cero");
        try
        {
            tree.ExpandToPath(@"C:\definitivamente-no-existe-xyz");
            Check(true, "nav: ruta mala no explota");
        }
        catch (Exception ex) { Check(false, "nav: ruta mala no explota (" + ex.GetType().Name + ")"); }
        try { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(sub)!, true); } catch { }
        try { System.IO.Directory.Delete(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "vb-navtest"), true); } catch { }
        tree.Dispose();
        return _failures == 0 ? 0 : 1;
    }

    private static unsafe int StageVirtual()
    {
        _failures = 0;
        Log("-- virtual");
        // descriptor fabricado: count=2 + nombres en offsets ABI (72, 592 stride)
        int total = 4 + 2 * 592;
        nint mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(total);
        try
        {
            byte* p = (byte*)mem;
            for (int i = 0; i < total; i++) p[i] = 0;
            *(uint*)p = 2;
            string n1 = "foto.png", n2 = "a/b\\mal:.txt";
            fixed (char* c1 = n1, c2 = n2)
            {
                char* d1 = (char*)(p + 4 + 72);
                char* d2 = (char*)(p + 4 + 592 + 72);
                for (int i = 0; i < n1.Length; i++) d1[i] = c1[i];
                for (int i = 0; i < n2.Length; i++) d2[i] = c2[i];
            }
            var names = Shell.ParseDescriptorNames((void*)mem);
            Check(names.Count == 2 && names[0] == "foto.png", $"virtual: parse x2 ({names.Count})");
            Check(names.Count == 2 && names[1] == "a/b\\mal:.txt", "virtual: parse crudo (sin sanitizar)");
            Check(Shell.SanitizeFileName("a/b\\mal:.txt") == "a_b_mal_.txt", "virtual: sanitize");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(mem); }
        // count=0 y count absurdo no explotan
        nint z = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
        try
        {
            *(uint*)(void*)z = 0;
            // second dword garbage para el caso absurdo
            *((uint*)(void*)z + 1) = 999;
            Check(Shell.ParseDescriptorNames((void*)z).Count == 0, "virtual: count 0");
            *(uint*)(void*)z = 999;
            Check(Shell.ParseDescriptorNames((void*)z).Count == 0, "virtual: count absurdo");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(z); }
        // sanitize directo: vacio -> imagen_<32hex>.png
        string gen = Shell.SanitizeFileName("  ");
        Check(gen.StartsWith("imagen_") && gen.EndsWith(".png") && gen.Length == 43, "virtual: nombre vacio genera png");
        // snapshot round-trip + dedup
        byte[] blob = [1, 2, 3, 4];
        string? s1 = Shell.SnapshotVirtualFile("a.bin", blob);
        string? s2 = Shell.SnapshotVirtualFile("a.bin", blob);
        Check(s1 is not null && System.IO.File.Exists(s1), "virtual: snapshot escribe");
        Check(s2 is not null && s2 != s1 && s2.EndsWith("a (1).bin")
            && System.IO.File.ReadAllBytes(s2).Length == 4, "virtual: dedup (1)");
        Check(Shell.SnapshotVirtualFile("x.bin", []) is null, "virtual: vacio -> null");
        try
        {
            if (s1 is not null) System.IO.File.Delete(s1);
            if (s2 is not null) System.IO.File.Delete(s2);
        }
        catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static int StageThumb()
    {
        _failures = 0;
        Log("-- thumb");
        // archivo real chico: el shell debe devolver pixels (icono o thumb)
        string f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-thumbtest.txt");
        System.IO.File.WriteAllText(f, "x");
        var px = Shell.GetThumbnailPixels(f, 64);
        Check(px is not null && px.Value.w == 64 && px.Value.h == 64, "thumb: txt 64px");
        if (px is not null)
        {
            bool any = false;
            unsafe
            {
                uint* p = (uint*)(void*)px.Value.buf;
                for (int i = 0; i < 64 * 64; i++) { if (p[i] != 0) { any = true; break; } }
            }
            Check(any, "thumb: buffer con contenido");
            Shell.FreeThumbnail(px.Value.buf);
        }
        Check(Shell.GetThumbnailPixels(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-noexiste-xyz.bin"), 64) is null,
            "thumb: missing -> null");
        Check(Shell.GetThumbnailPixels(f, 8) is null, "thumb: size chico -> null");
        try { System.IO.File.Delete(f); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static int StagePrune()
    {
        _failures = 0;
        Log("-- prune");
        // PruneMissing no necesita ventana (App.Instance null-safe)
        var engine = new ViennaBar.DropEngine(new ViennaBar.ShellTree());
        string live = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-prunetest.txt");
        System.IO.File.WriteAllText(live, "x");
        string dead = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-prunetest-dead.txt");
        engine.DropStack.Add(dead);
        engine.DropStack.Add(live);
        engine.DropStack.Add(dead);
        engine.PruneMissing();
        Check(engine.DropStack.Count == 1 && engine.DropStack[0] == live, "prune: saca muertos, deja vivos");
        engine.PruneMissing();
        Check(engine.DropStack.Count == 1, "prune: idempotente");
        try { System.IO.File.Delete(live); } catch { }
        engine.Dispose();
        return _failures == 0 ? 0 : 1;
    }

    private static int StageSnap()
    {
        _failures = 0;
        Log("-- snap");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-snapping");
        System.IO.Directory.CreateDirectory(dir);
        string src = System.IO.Path.Combine(dir, "orig.txt");
        System.IO.File.WriteAllText(src, "snap-me");
        string s1 = Shell.SnapshotRealFile(src);
        Check(s1 != src && System.IO.File.Exists(s1)
            && System.IO.File.ReadAllText(s1) == "snap-me"
            && s1.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            "snap: copia independiente en temp");
        // inmunidad: borrar el original no afecta al snapshot
        System.IO.File.Delete(src);
        Check(System.IO.File.Exists(s1), "snap: sobrevive al original");
        foreach (var f in System.IO.Directory.GetFiles(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ViennaBar", "drop")))
            try { System.IO.File.Delete(f); } catch { }
        // dirs y faltantes van por referencia
        Check(Shell.SnapshotRealFile(dir) == dir, "snap: dir por referencia");
        Check(Shell.SnapshotRealFile(System.IO.Path.Combine(dir, "noexiste.txt"))
            .EndsWith("noexiste.txt"), "snap: faltante por referencia");
        // tope de tamano via cap chico
        System.IO.File.WriteAllText(src, "1234567890");
        Check(Shell.SnapshotRealFile(src, 5) == src, "snap: sobre tope por referencia");
        // wipe de arranque
        Shell.ClearDropSnapshots();
        Check(!System.IO.Directory.Exists(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ViennaBar", "drop")), "snap: wipe");
        try { System.IO.Directory.Delete(dir, true); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static int StageFindEx()
    {
        _failures = 0;
        Log("-- findex (solo perfil/known-folders, temp)");
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-findex");
        try { System.IO.Directory.Delete(root, true); } catch { }
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "alpha", "beta"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "alfa2"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "other"));
        string hid = System.IO.Path.Combine(root, "hiddendir");
        System.IO.Directory.CreateDirectory(hid);
        new System.IO.DirectoryInfo(hid).Attributes |= System.IO.FileAttributes.Hidden;
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "unarchivo.txt"), "x");
        var list = new List<ViennaBar.FolderIndex.DirEntry>();
        ViennaBar.FolderIndex.BuildFromRoots(new[] { root }, list);
        Check(ViennaBar.FolderIndex.SearchIn(list, "beta").Exists(e => e.FullPath.EndsWith("alpha\\beta")), "findex: anidado");
        Check(ViennaBar.FolderIndex.SearchIn(list, "hidden").Count == 0, "findex: ocultos fuera");
        Check(ViennaBar.FolderIndex.SearchIn(list, "unarchivo").Count == 0, "findex: archivos fuera (solo dirs)");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "match"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "prefix-match"));
        list.Clear();
        ViennaBar.FolderIndex.BuildFromRoots(new[] { root }, list);
        var r = ViennaBar.FolderIndex.SearchIn(list, "match");
        Check(r.Count >= 2 && r[0].Name == "match" && r[1].Name == "prefix-match", "findex: rank exacto primero");
        Check(ViennaBar.FolderIndex.SearchIn(list, "").Count == 0, "findex: query vacia");
        Check(ViennaBar.FolderIndex.SearchIn(list, "zzz-noexiste").Count == 0, "findex: sin match");
        // acentos: "imagenes" encuentra "Imágenes" (y viceversa)
        var acc = new List<ViennaBar.FolderIndex.DirEntry>
        {
            new("Imágenes", "C:\\ Fotos\\Imágenes"),
            new("Otra", "C:\\x"),
        };
        Check(ViennaBar.FolderIndex.SearchIn(acc, "imagenes").Count == 1, "findex: fold acentos");
        Check(ViennaBar.FolderIndex.SearchIn(acc, "IMÁGENES")[0].Name == "Imágenes", "findex: exacto con acento");
        // alias españoles resuelven a paths reales existentes
        var aliases = ViennaBar.FolderIndex.GetAliases();
        Check(aliases.Exists(a => a.Name == "Descargas" && System.IO.Directory.Exists(a.FullPath)),
            "findex: alias Descargas");
        Check(aliases.TrueForAll(a => System.IO.Directory.Exists(a.FullPath)), "findex: alias existen");
        // UserRoots: existen y sin duplicados
        var roots = ViennaBar.FolderIndex.UserRoots();
        Check(roots.Count > 0 && roots.TrueForAll(System.IO.Directory.Exists), "findex: UserRoots existen");
        Check(roots.Count == new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase).Count,
            "findex: UserRoots dedup");
        try { System.IO.Directory.Delete(root, true); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static ViennaBar.ShellTree.TreeNode? FindNode(List<ViennaBar.ShellTree.TreeNode> list, string parsing)
    {
        foreach (var n in list)
        {
            if (n.ParsingName.TrimEnd('\\').Equals(parsing.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return n;
            var d = FindNode(n.Children, parsing);
            if (d is not null) return d;
        }
        return null;
    }

    private static int StageLauncher()
    {
        _failures = 0;
        Log("-- launcher");
        Check(ViennaBar.Launcher.Launcher.Classify([]).Kind == ViennaBar.Launcher.TargetKind.Reveal,
            "launcher: sin args -> Reveal");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-launchertest");
        string sub = System.IO.Path.Combine(dir, "sub");
        string file = System.IO.Path.Combine(dir, "a.txt");
        System.IO.Directory.CreateDirectory(sub);
        System.IO.File.WriteAllText(file, "x");
        var t = ViennaBar.Launcher.Launcher.Classify([sub]);
        Check(t.Kind == ViennaBar.Launcher.TargetKind.Folder
            && t.Path == System.IO.Path.GetFullPath(sub), "launcher: carpeta -> Folder");
        Check(ViennaBar.Launcher.Launcher.Classify([file]).Kind == ViennaBar.Launcher.TargetKind.Passthrough,
            "launcher: archivo -> Passthrough");
        Check(ViennaBar.Launcher.Launcher.Classify(["/e"]).Kind == ViennaBar.Launcher.TargetKind.Passthrough,
            "launcher: switch -> Passthrough");
        Check(ViennaBar.Launcher.Launcher.Classify(["-Embedding"]).Kind == ViennaBar.Launcher.TargetKind.Passthrough,
            "launcher: flag -> Passthrough");
        Check(ViennaBar.Launcher.Launcher.Classify(["shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"]).Kind
            == ViennaBar.Launcher.TargetKind.Passthrough, "launcher: shell: -> Passthrough");
        Check(ViennaBar.Launcher.Launcher.Classify(["search-ms:query=x"]).Kind
            == ViennaBar.Launcher.TargetKind.Passthrough, "launcher: search-ms -> Passthrough");
        Check(ViennaBar.Launcher.Launcher.Classify([System.IO.Path.Combine(dir, "noexiste")]).Kind
            == ViennaBar.Launcher.TargetKind.Passthrough, "launcher: inexistente -> Passthrough");
        Check(ViennaBar.Launcher.Launcher.QuoteArgs(["a", "b c", "d\"e"]) == "a \"b c\" \"d\\\"e\"",
            "launcher: QuoteArgs");
        // IFEO antepone la imagen original: se quita antes de clasificar
        Check(ViennaBar.Launcher.Launcher.StripImageName([]).Length == 0, "launcher: strip vacio");
        Check(ViennaBar.Launcher.Launcher.StripImageName(["explorer.exe"]).Length == 0
            && ViennaBar.Launcher.Launcher.Classify(
                ViennaBar.Launcher.Launcher.StripImageName(["explorer.exe"])).Kind
                == ViennaBar.Launcher.TargetKind.Reveal, "launcher: strip Win+E -> Reveal");
        Check(ViennaBar.Launcher.Launcher.Classify(
                ViennaBar.Launcher.Launcher.StripImageName(["explorer.exe", sub])).Kind
            == ViennaBar.Launcher.TargetKind.Folder, "launcher: strip + carpeta -> Folder");
        Check(ViennaBar.Launcher.Launcher.StripImageName([@"C:\Windows\explorer.exe", "/e"])[0] == "/e",
            "launcher: strip path completo");
        try { System.IO.Directory.Delete(dir, true); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static int StageM2Ifeo()
    {
        _failures = 0;
        Log("-- m2ifeo (sandbox HKCU, sin elevacion, sin HKLM)");
        string sb = @"Software\ViennaBarTests_M2";
        string ifeo = sb + @"\IFEO";
        string backup = sb + @"\Backup";
        var hive = Microsoft.Win32.Registry.CurrentUser;
        try { hive.DeleteSubKeyTree(sb, false); } catch { }
        // seed: Debugger pre-existente (ej. otra herramienta)
        using (var k = hive.CreateSubKey(ifeo + @"\explorer.exe"))
        {
            k?.SetValue("Debugger", "old-dbg.exe");
        }
        string blobDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-m2test");
        System.IO.Directory.CreateDirectory(blobDir);
        string target = System.IO.Path.Combine(blobDir, "target.txt");
        System.IO.File.WriteAllText(target, "x");
        string link = System.IO.Path.Combine(blobDir, "link.exe");
        string regPath = System.IO.Path.Combine(blobDir, "m2.reg");
        string launcher = System.IO.Path.Combine(blobDir, "launcher.exe");
        System.IO.File.WriteAllText(launcher, "x");
        Check(ViennaBar.Integration.ApplyM2(hive, ifeo, hive, backup,
            System.IO.Path.Combine(blobDir, "noexiste.exe"), link, target, regPath, requireAdmin: false)
            .Contains("no encontrado"), "m2: sin launcher aborta");
        string summary = ViennaBar.Integration.ApplyM2(hive, ifeo, hive, backup,
            launcher, link, target, regPath, requireAdmin: false);
        Check(summary.Contains("M2 aplicado"), "m2: apply resumen");
        using (var k = hive.OpenSubKey(ifeo + @"\explorer.exe", false))
            Check(k is not null && (k.GetValue("Debugger") as string) == $"\"{launcher}\"",
                "m2: Debugger citado");
        Check(System.IO.File.Exists(link), "m2: copia creada");
        using (var k = hive.OpenSubKey(backup, false))
            Check(k is not null && (k.GetValue("HadKey") as int?) == 1
                && (k.GetValue("Debugger") as string) == "old-dbg.exe"
                && (k.GetValue("LinkCreated") as int?) == 1, "m2: backup write-once ready");
        string reg = System.IO.File.ReadAllText(regPath);
        Check(reg.Contains("HKEY_LOCAL_MACHINE") && reg.Contains("old-dbg.exe") && reg.Contains("ADMIN"),
            "m2: rescue .reg");
        string summary2 = ViennaBar.Integration.ApplyM2(hive, ifeo, hive, backup,
            launcher, link, target, regPath, requireAdmin: false);
        Check(summary2.Contains("conservado"), "m2: segundo apply conserva backup");
        using (var k = hive.OpenSubKey(backup, false))
            Check(k is not null && (k.GetValue("Debugger") as string) == "old-dbg.exe",
                "m2: backup write-once");
        string back = ViennaBar.Integration.RevertM2(hive, ifeo, hive, backup);
        Check(back.Contains("M2 revertido"), "m2: revert resumen");
        using (var k = hive.OpenSubKey(ifeo + @"\explorer.exe", false))
            Check(k is not null && (k.GetValue("Debugger") as string) == "old-dbg.exe",
                "m2: Debugger restaurado");
        Check(!System.IO.File.Exists(link), "m2: copia propia se borra");
        Check(hive.OpenSubKey(backup, false) is null, "m2: backup se borra");
        Check(ViennaBar.Integration.RevertM2(hive, ifeo, hive, backup).Contains("nada que revertir"),
            "m2: revert sin backup");
        try { hive.DeleteSubKeyTree(sb, false); } catch { }
        try { System.IO.Directory.Delete(blobDir, true); } catch { }
        return _failures == 0 ? 0 : 1;
    }
}
