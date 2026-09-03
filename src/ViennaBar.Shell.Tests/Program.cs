using Shell = ViennaBar.ShellNative.ShellNative;

namespace ViennaBarTests;

// Test AOT del pipeline de launch paso a paso â€” bisect del 0xC0000005.
// InternalsVisibleTo llega por transitivity? No: lo agregamos al Shell csproj.
internal static class Program
{
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
                    // TrackPopupMenu modal — no sirve para test; reproducimos el
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
}
