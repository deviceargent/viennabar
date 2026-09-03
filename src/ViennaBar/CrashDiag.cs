using System.Runtime.InteropServices;

namespace ViennaBar;

// CrashDiag — VEH (vectored exception handler) para 0xC0000005 en AOT.
// Un AV nativo mata el proceso sin pasar por UnhandledException; el VEH
// corre ANTES de que el proceso muera y deja module+RVA+minidump en
// %TEMP%\viennabar-crash.log / viennabar-crash.dmp.
// Todo P/Invoke crudo: no genera tipos CsWin32 (assembly core sin config).
internal static unsafe class CrashDiag
{
    private const string LogPath = "viennabar-crash.log";
    private const string DumpPath = "viennabar-crash.dmp";

    private static void* _veh;
    private static bool _dumping;

    [DllImport("kernel32.dll")]
    private static extern void* AddVectoredExceptionHandler(uint first, delegate* unmanaged<EXCEPTION_POINTERS*, int> handler);

    [DllImport("kernel32.dll")]
    private static extern uint RemoveVectoredExceptionHandler(void* handle);

    [UnmanagedCallersOnly]
    private static int VectoredHandler(EXCEPTION_POINTERS* info)
    {
        var code = info->ExceptionRecord->ExceptionCode;
        // 0x80000003 breakpoint / 0x4000001F single-step: no son fatales, skip
        if (code == unchecked((int)0x80000003) || code == 0x4000001F) return 0;
        if (_dumping) return 0;
        _dumping = true;
        LogLine($"[{DateTime.Now:HH:mm:ss.fff}] EXC nativo code=0x{code:X8}");
        try
        {
            var rec = info->ExceptionRecord;
            var addr = (nint)rec->ExceptionAddress;
            LogLine($"  faulting ip = 0x{addr:X} {ModuleOf(addr)}");
            if (code == unchecked((int)0xC0000005))
                LogLine($"  access=0x{(int)rec->ExceptionInformation[0]} target=0x{(nint)rec->ExceptionInformation[1]:X}");
            WriteMinidump();
        }
        catch { }
        _dumping = false;
        return 0; // CONTINUE_SEARCH: la excepcion sigue su curso, la evidencia ya quedo
    }

    // ---- que modulo + RVA ----
    [DllImport("psapi.dll")]
    private static extern bool EnumProcessModules(void* hProcess, void** lphModule, uint cb, uint* lpcbNeeded);

    [DllImport("psapi.dll")]
    private static extern bool GetModuleInformation(void* hProcess, void* hModule, out MODULEINFO lpmodinfo, uint cb);

    [DllImport("psapi.dll")]
    private static extern uint GetModuleFileNameExW(void* hProcess, void* hModule, char* lpFilename, uint nSize);

    [DllImport("kernel32.dll")]
    private static extern void* GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO { public nint lpBaseOfDll; public nuint SizeOfImage; public nint EntryPoint; }

    private static string ModuleOf(nint addr)
    {
        var hMods = stackalloc void*[128];
        uint cbNeeded = 0;
        var proc = GetCurrentProcess();   // pseudo-handle: sirve para queries de psapi
        if (!EnumProcessModules(proc, hMods, (uint)(sizeof(void*) * 128), &cbNeeded)) return "(enum fail)";
        int count = (int)(cbNeeded / sizeof(void*));
        var name = stackalloc char[260];
        for (int i = 0; i < count; i++)
        {
            if (!GetModuleInformation(proc, hMods[i], out var mi, (uint)sizeof(MODULEINFO))) continue;
            var base_ = (nint)mi.lpBaseOfDll;
            if (addr >= base_ && addr < base_ + (nint)mi.SizeOfImage)
            {
                var n = (int)GetModuleFileNameExW(proc, hMods[i], name, 260);
                var full = n > 0 ? new string(name, 0, n) : "?";
                var shortName = full[(full.LastIndexOf('\\') + 1)..];
                return $"module={shortName}+0x{addr - base_:X}";
            }
        }
        return "(ip fuera de modulos)";
    }

    // ---- minidump via dbghelp ----
    [DllImport("dbghelp.dll")]
    private static extern bool MiniDumpWriteDump(void* hProcess, uint pid, void* hFile, uint dumpType, void* exceptionParam, void* userStreamParam, void* callbackParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern void* CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, void* lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, void* hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(void* hObject);

    private static void WriteMinidump()
    {
        const uint GENERIC_WRITE = 0x40000000;
        const uint CREATE_ALWAYS = 2;
        const uint MiniDumpNormal = 0;
        var hFile = CreateFileW(Path.Combine(Path.GetTempPath(), DumpPath), GENERIC_WRITE, 0, null, CREATE_ALWAYS, 0, null);
        if (hFile is null || (nint)hFile == -1) return;
        _ = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), hFile, MiniDumpNormal, null, null, null);
        _ = CloseHandle(hFile);
        LogLine($"  minidump -> {Path.Combine(Path.GetTempPath(), DumpPath)}");
    }

    // ---- tipos crudos ----
    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_POINTERS { public EXCEPTION_RECORD* ExceptionRecord; public void* ContextRecord; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_RECORD
    {
        public int ExceptionCode;
        public uint ExceptionFlags;
        public void* ExceptionRecord;
        public void* ExceptionAddress;
        public uint NumberParameters;
        public fixed ulong ExceptionInformation[15];
    }

    private static void LogLine(string s)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), LogPath), s + "\n"); }
        catch { }
    }

    public static void Init()
    {
        delegate* unmanaged<EXCEPTION_POINTERS*, int> h = &VectoredHandler;
        _veh = AddVectoredExceptionHandler(1, h);
        LogLine($"[{DateTime.Now:HH:mm:ss.fff}] VEH installed");
    }
}
