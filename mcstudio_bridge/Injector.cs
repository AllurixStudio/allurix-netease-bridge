// Allurix.MCStudio.Injector.cs
// x86 .NET EXE that injects a DLL into MCStudio via CreateRemoteThread + LoadLibraryW.
// Must be run as x86 to match MCStudio's bitness.
//
// Usage: Allurix.MCStudio.Injector.exe <dll-path> [--pid <pid>]

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class Injector
{
    const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_RESERVE = 0x2000;
    const uint MEM_RELEASE = 0x8000;
    const uint PAGE_READWRITE = 0x04;
    const uint WAIT_OBJECT_0 = 0x00000000;
    const uint WAIT_TIMEOUT = 0x00000102;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr hProc, IntPtr addr, uint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr hProc, IntPtr addr, byte[] buf, uint size, out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateRemoteThread(IntPtr hProc, IntPtr attr, uint stack, IntPtr start, IntPtr param, uint flags, out uint tid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeThread(IntPtr handle, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFreeEx(IntPtr hProc, IntPtr addr, uint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: Allurix.MCStudio.Injector.exe <dll-path> [--pid <pid>]");
            return 1;
        }

        string dllPath = Path.GetFullPath(args[0]);
        if (!File.Exists(dllPath))
        {
            Console.WriteLine("ERROR: DLL not found: " + dllPath);
            return 1;
        }

        int requestedPid = 0;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--pid" && (i + 1 >= args.Length || !int.TryParse(args[i + 1], out requestedPid)))
            {
                Console.WriteLine("ERROR: Invalid --pid value.");
                return 1;
            }
        }

        Process[] procs = Process.GetProcessesByName("MCStudio");
        if (procs.Length == 0)
        {
            Console.WriteLine("ERROR: MCStudio is not running.");
            return 1;
        }

        Process target = null;
        if (requestedPid > 0)
        {
            try { target = Process.GetProcessById(requestedPid); }
            catch (ArgumentException) { }
            if (target == null || target.ProcessName != "MCStudio")
            {
                Console.WriteLine("ERROR: --pid is not a running MCStudio process.");
                return 1;
            }
        }
        else target = procs[0];

        int pid = target.Id;
        Console.WriteLine("MCStudio PID: " + pid);
        Console.WriteLine("DLL: " + dllPath);

        IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
        if (hProc == IntPtr.Zero)
        {
            Console.WriteLine("ERROR: OpenProcess failed: " + Marshal.GetLastWin32Error());
            return 1;
        }

        IntPtr remoteMem = IntPtr.Zero;
        IntPtr hThread = IntPtr.Zero;
        try
        {
            byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            remoteMem = VirtualAllocEx(
                hProc,
                IntPtr.Zero,
                (uint)pathBytes.Length,
                MEM_COMMIT | MEM_RESERVE,
                PAGE_READWRITE
            );
            if (remoteMem == IntPtr.Zero)
            {
                Console.WriteLine("ERROR: VirtualAllocEx failed: " + Marshal.GetLastWin32Error());
                return 1;
            }

            uint written;
            if (!WriteProcessMemory(hProc, remoteMem, pathBytes, (uint)pathBytes.Length, out written) ||
                written != pathBytes.Length)
            {
                Console.WriteLine("ERROR: WriteProcessMemory failed: " + Marshal.GetLastWin32Error());
                return 1;
            }

            IntPtr kernel32 = GetModuleHandleA("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
            {
                Console.WriteLine("ERROR: GetModuleHandleA failed: " + Marshal.GetLastWin32Error());
                return 1;
            }
            IntPtr loadLibW = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibW == IntPtr.Zero)
            {
                Console.WriteLine("ERROR: GetProcAddress failed: " + Marshal.GetLastWin32Error());
                return 1;
            }

            uint threadId;
            hThread = CreateRemoteThread(
                hProc,
                IntPtr.Zero,
                0,
                loadLibW,
                remoteMem,
                0,
                out threadId
            );
            if (hThread == IntPtr.Zero)
            {
                Console.WriteLine("ERROR: CreateRemoteThread failed: " + Marshal.GetLastWin32Error());
                return 1;
            }

            uint waitResult = WaitForSingleObject(hThread, 15000);
            if (waitResult != WAIT_OBJECT_0)
            {
                if (waitResult == WAIT_TIMEOUT)
                    Console.WriteLine("ERROR: LoadLibraryW timed out after 15 seconds.");
                else
                    Console.WriteLine("ERROR: WaitForSingleObject failed: " + Marshal.GetLastWin32Error());

                // The remote thread can still be reading this buffer. Its process owns
                // the allocation until exit, so do not free it from a timed-out injector.
                remoteMem = IntPtr.Zero;
                return 1;
            }

            uint exitCode;
            if (!GetExitCodeThread(hThread, out exitCode))
            {
                Console.WriteLine("ERROR: GetExitCodeThread failed: " + Marshal.GetLastWin32Error());
                return 1;
            }
            if (exitCode == 0)
            {
                Console.WriteLine("ERROR: LoadLibraryW returned NULL");
                return 1;
            }

            Console.WriteLine("OK: DLL loaded, handle = 0x" + exitCode.ToString("X8"));
            return 0;
        }
        finally
        {
            if (hThread != IntPtr.Zero) CloseHandle(hThread);
            if (remoteMem != IntPtr.Zero) VirtualFreeEx(hProc, remoteMem, 0, MEM_RELEASE);
            CloseHandle(hProc);
        }
    }
}