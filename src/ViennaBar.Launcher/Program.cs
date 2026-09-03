using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ViennaBar.Launcher;

// M2 — wrapper IFEO de explorer.exe. Sin dependencias (BCL + DllImport
// crudo): arranque frio <50ms y AOT trivial.
//   carpeta        -> forward a la barra viva (WM_COPYDATA) o cold-start
//   sin args       -> revelar barra (cubre Win+E)
//   archivo suelto -> ShellExecute directo (fiel, sin explorer)
//   resto          -> passthrough al explorer real via hardlink (setup lo crea;
//                    sin hardlink: no hay nada fiel que hacer -> salir 0)
internal static class Program
{
    private static int Main(string[] args)
    {
        var t = Launcher.Classify(args);
        string dir = AppDir();
        return t.Kind switch
        {
            TargetKind.Folder => Launcher.OpenFolder(t.Path!, dir),
            TargetKind.Reveal => Launcher.RevealBar(dir),
            _ => Launcher.Passthrough(args, dir),
        };
    }

    private static string AppDir() =>
        Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
}

internal enum TargetKind { Reveal, Folder, Passthrough }

internal sealed record Target(TargetKind Kind, string? Path);

internal static unsafe class Launcher
{
    internal static Target Classify(string[] args)
    {
        if (args.Length == 0) return new(TargetKind.Reveal, null);
        string a0 = args[0];
        if (a0.StartsWith('/') || a0.StartsWith('-') || a0.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || a0.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase)
            || a0.StartsWith("::{") || a0.StartsWith("{"))
            return new(TargetKind.Passthrough, null);
        string full;
        try { full = Path.GetFullPath(a0); }
        catch { return new(TargetKind.Passthrough, null); }
        if (Directory.Exists(full)) return new(TargetKind.Folder, full);
        return new(TargetKind.Passthrough, null);   // archivo o inexistente: fiel al explorer
    }

    internal static string QuoteArgs(string[] args)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (a.Length > 0 && a.IndexOfAny([' ', '"', '\t']) < 0) sb.Append(a);
            else sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
        }
        return sb.ToString();
    }

    internal static int OpenFolder(string path, string appDir)
    {
        if (ForwardToBar(path)) return 0;
        // cold-start: la barra arranca y navega via s_pendingOpenFolder
        string bar = Path.Combine(appDir, "ViennaBar.exe");
        if (File.Exists(bar))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = bar,
                    Arguments = $"--open-folder \"{path}\"",
                    UseShellExecute = false,
                });
                return 0;
            }
            catch { }
        }
        return Passthrough([path], appDir);
    }

    internal static int RevealBar(string appDir)
    {
        if (ForwardToBar("")) return 0;
        string bar = Path.Combine(appDir, "ViennaBar.exe");
        if (File.Exists(bar))
        {
            try { Process.Start(new ProcessStartInfo { FileName = bar, UseShellExecute = false }); return 0; }
            catch { }
        }
        return 0;
    }

    internal static int Passthrough(string[] args, string appDir)
    {
        string link = Path.Combine(appDir, "explorer-vb.exe");
        if (File.Exists(link)) return Run(link, QuoteArgs(args));
        // degradacion fiel sin hardlink: archivo -> su handler default
        if (args.Length > 0 && File.Exists(args[0]) && !Directory.Exists(args[0]))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = args[0], UseShellExecute = true });
                return 0;
            }
            catch { }
        }
        return 0;
    }

    private static int Run(string exe, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = arguments, UseShellExecute = false });
            return 0;
        }
        catch { return 1; }
    }

    // ---- forward a la barra viva (duplicado minimo de SingleInstance:
    // el Launcher no referencia al core a proposito) ----
    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataMsg
    {
        public nuint dwData;
        public uint cbData;
        public void* lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowW(string? cls, string? title);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern uint SendMessageTimeoutRaw(void* hWnd, uint msg, nuint wParam, void* lParam,
        uint fuFlags, uint timeout, nuint* result);

    internal static bool ForwardToBar(string path)
    {
        try
        {
            nint hwnd = FindWindowW("ViennaBarSidebar", null);
            if (hwnd == 0) return false;
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(path + '\0');
            fixed (byte* pb = bytes)
            {
                var cds = new CopyDataMsg { dwData = 1, cbData = (uint)bytes.Length, lpData = pb };
                nuint res = 0;
                _ = SendMessageTimeoutRaw((void*)hwnd, 0x004A, 0, &cds, 2, 5000, &res);
            }
            return true;
        }
        catch { return false; }
    }
}
