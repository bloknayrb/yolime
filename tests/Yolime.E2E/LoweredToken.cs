using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Yolime.E2E;

/// <summary>
/// Starts a process the way a normal (non-elevated) sign-in would: the harness's own token, restricted so that
/// BUILTIN\Administrators is deny-only (IsInRole and Inno's IsAdmin both read false), at Medium integrity. Windows
/// Sandbox's only account is an administrator, so without this the copy under test would run elevated, and yolime
/// refuses to update an elevated copy.
/// </summary>
internal static class LoweredToken
{
    private const string Administrators = "S-1-5-32-544";
    private const string MediumIntegrity = "S-1-16-8192";
    private const uint SeGroupUseForDenyOnly = 0x10;
    private const uint SeGroupIntegrity = 0x20;
    private const int TokenGroups = 2;
    private const int TokenIntegrityLevel = 25;

    /// <summary>
    /// Records the window station's and desktop's descriptors (SDDL) for diagnosis, and grants the account's own SID full
    /// access with a Medium label. Not the cause of the 0xC0000142 seen first: the descriptors already granted the logon
    /// SID, and that failure stayed until <see cref="SetDefaultDacl"/>. Whether this grant is needed at all is untested.
    /// </summary>
    public static string PrepareDesktop()
    {
        var log = new System.Text.StringBuilder();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var station = OpenWindowStationW("WinSta0", false, 0x00020000 | 0x00040000 | 0x00080000);
        if (station == 0) log.AppendLine($"winsta0: open failed {Marshal.GetLastWin32Error()}");
        else
        {
            Grant("winsta0", station, user, log);
            CloseWindowStation(station);
        }
        var desktop = OpenDesktopW("Default", 0, false, 0x00020000 | 0x00040000 | 0x00080000); // READ_CONTROL | WRITE_DAC | WRITE_OWNER
        if (desktop == 0) log.AppendLine($"desktop: open failed {Marshal.GetLastWin32Error()}");
        else
        {
            Grant("desktop", desktop, user, log);
            CloseDesktop(desktop);
        }
        return log.ToString();
    }

    private static void Grant(string what, nint handle, string user, System.Text.StringBuilder log)
    {
        const uint Dacl = 0x4, Label = 0x10;
        const int WindowObject = 7;
        var err = GetSecurityInfo(handle, WindowObject, Dacl | Label, 0, 0, 0, 0, out var sd);
        if (err != 0) { log.AppendLine($"{what}: read failed {err}"); return; }
        ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, Dacl | Label, out var sddlPtr, out _);
        var sddl = Marshal.PtrToStringUni(sddlPtr) ?? "";
        LocalFree(sddlPtr);
        LocalFree(sd);
        log.AppendLine($"{what} before: {sddl}");
        var dacl = sddl.StartsWith("D:", StringComparison.Ordinal) ? (sddl.IndexOf("S:", StringComparison.Ordinal) is var s and > 0 ? sddl[..s] : sddl) : "D:";
        var updated = dacl + $"(A;;GA;;;{user})" + "S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(updated, 1, out var newSd, out _)) { log.AppendLine($"{what}: build failed {Marshal.GetLastWin32Error()}"); return; }
        try
        {
            GetSecurityDescriptorDacl(newSd, out _, out var newDacl, out _);
            GetSecurityDescriptorSacl(newSd, out _, out var newSacl, out _);
            log.AppendLine($"{what} dacl: {SetSecurityInfo(handle, WindowObject, Dacl, 0, 0, newDacl, 0)}, label: {SetSecurityInfo(handle, WindowObject, Label, 0, 0, 0, newSacl)}");
        }
        finally
        {
            LocalFree(newSd);
        }
    }

    /// <summary>Starts <paramref name="exe"/> lowered; returns its pid.</summary>
    public static int Start(string exe, string? arguments, string workingDirectory)
    {
        var (pid, process) = Launch(exe, arguments, workingDirectory);
        CloseHandle(process);
        return pid;
    }

    /// <summary>Runs <paramref name="exe"/> lowered and waits: its exit code, or -1 if it ran past the timeout (then killed).</summary>
    public static int Run(string exe, string arguments, string workingDirectory, int timeoutMs)
    {
        var (_, process) = Launch(exe, arguments, workingDirectory);
        try
        {
            if (WaitForSingleObject(process, (uint)timeoutMs) != 0)
            {
                TerminateProcess(process, 1);
                return -1;
            }
            return GetExitCodeProcess(process, out var code) ? (int)code : -1;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static (int Pid, nint Process) Launch(string exe, string? arguments, string workingDirectory)
    {
        var self = OpenOwnToken();
        try
        {
            if (!ConvertStringSidToSidW(Administrators, out var admins)) throw new Win32Exception();
            nint restricted;
            try
            {
                var disable = new[] { new SidAndAttributes { Sid = admins, Attributes = 0 } };
                if (!CreateRestrictedToken(self, 0, 1, disable, 0, 0, 0, 0, out restricted)) throw new Win32Exception();
            }
            finally
            {
                LocalFree(admins);
            }
            try
            {
                SetMedium(restricted);
                SetDefaultDacl(restricted);
                EnablePrivilege("SeIncreaseQuotaPrivilege");
                var si = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                // Writable, as CreateProcess requires of its command line.
                var commandLine = new System.Text.StringBuilder("\"" + exe + "\"" + (arguments is null ? "" : " " + arguments));
                // A restricted copy of the caller's own token needs no SeAssignPrimaryTokenPrivilege.
                if (!CreateProcessAsUserW(restricted, exe, commandLine, 0, 0, false, 0, 0, workingDirectory, ref si, out var pi))
                    throw new Win32Exception();
                CloseHandle(pi.Thread);
                return (pi.ProcessId, pi.Process);
            }
            finally
            {
                CloseHandle(restricted);
            }
        }
        finally
        {
            CloseHandle(self);
        }
    }

    /// <summary>
    /// What a running process's token says: its integrity RID (0x2000 Medium, 0x3000 High) and whether
    /// Administrators is in it only for deny. Null when it can't be read.
    /// </summary>
    public static (int Integrity, bool AdminsDenyOnly, bool AdminsEnabled)? Read(int pid)
    {
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, 0x0008, out var token)) return null; // TOKEN_QUERY
            try
            {
                var integrity = Integrity(token);
                if (!ConvertStringSidToSidW(Administrators, out var admins)) return null;
                try
                {
                    var (denyOnly, enabled) = GroupState(token, admins);
                    return (integrity, denyOnly, enabled);
                }
                finally
                {
                    LocalFree(admins);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Null when the token is what a lowered launch should give; otherwise what's wrong with it.</summary>
    public static string? CheckLowered(int pid) => Read(pid) switch
    {
        null => "couldn't read the copy's token",
        { Integrity: not 0x2000 } t => $"the copy runs at integrity 0x{t.Integrity:X}, not Medium",
        { AdminsDenyOnly: false } => "Administrators isn't deny-only in the copy's token",
        _ => null,
    };

    private static nint OpenOwnToken()
    {
        // TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_PRIVILEGES
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008 | 0x0002 | 0x0001 | 0x0080 | 0x0020, out var token)) throw new Win32Exception();
        return token;
    }

    /// <summary>
    /// An elevated token's default DACL (what the new process's own objects get) grants Administrators and SYSTEM only.
    /// With Administrators deny-only, the process couldn't open itself and would die in DLL init (0xC0000142), so its
    /// objects grant the account itself instead, as a normal sign-in's token does.
    /// </summary>
    private static void SetDefaultDacl(nint token)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:(A;;GA;;;{user})(A;;GA;;;SY)", 1, out var sd, out _)) throw new Win32Exception();
        try
        {
            GetSecurityDescriptorDacl(sd, out _, out var dacl, out _);
            var buffer = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(buffer, dacl); // TOKEN_DEFAULT_DACL { PACL DefaultDacl }
                if (!SetTokenInformation(token, 6, buffer, IntPtr.Size)) throw new Win32Exception(); // TokenDefaultDacl
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            LocalFree(sd);
        }
    }

    private static void SetMedium(nint token)
    {
        if (!ConvertStringSidToSidW(MediumIntegrity, out var sid)) throw new Win32Exception();
        try
        {
            var label = new TokenMandatoryLabel { Label = new SidAndAttributes { Sid = sid, Attributes = SeGroupIntegrity } };
            var size = Marshal.SizeOf<TokenMandatoryLabel>() + GetLengthSid(sid);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(label, buffer, false);
                if (!SetTokenInformation(token, TokenIntegrityLevel, buffer, size)) throw new Win32Exception();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            LocalFree(sid);
        }
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 }; // SE_PRIVILEGE_ENABLED
            AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static int Integrity(nint token)
    {
        GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var size);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) return -1;
            var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (bool DenyOnly, bool Enabled) GroupState(nint token, nint sid)
    {
        GetTokenInformation(token, TokenGroups, 0, 0, out var size);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenGroups, buffer, size, out _)) return (false, false);
            var count = Marshal.ReadInt32(buffer);
            var entry = Marshal.SizeOf<SidAndAttributes>();
            // TOKEN_GROUPS: a DWORD count, then the array aligned to a pointer.
            var first = buffer + IntPtr.Size;
            for (var i = 0; i < count; i++)
            {
                var group = Marshal.PtrToStructure<SidAndAttributes>(first + i * entry);
                if (!EqualSid(group.Sid, sid)) continue;
                return ((group.Attributes & SeGroupUseForDenyOnly) != 0, (group.Attributes & 0x4) != 0); // SE_GROUP_ENABLED
            }
            return (false, false);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertStringSidToSidW(string sid, out nint psid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(nint token, uint flags, uint disableCount, SidAndAttributes[] disable, uint deleteCount, nint delete, uint restrictCount, nint restrict, out nint newToken);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(nint token, int cls, nint info, int length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int cls, nint info, int length, out int returned);
    [DllImport("advapi32.dll")] private static extern int GetLengthSid(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("advapi32.dll")] private static extern bool EqualSid(nint a, nint b);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges state, int length, nint previous, nint returned);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(nint token, string app, System.Text.StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inherit,
        uint flags, nint environment, string cwd, ref StartupInfo si, out ProcessInformation pi);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint OpenWindowStationW(string name, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseWindowStation(nint station);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint OpenDesktopW(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(nint handle, int type, uint info, nint owner, nint group, nint dacl, nint sacl, out nint sd);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(nint handle, int type, uint info, nint owner, nint group, nint dacl, nint sacl);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(nint sd, uint revision, uint info, out nint sddl, out int length);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint sd, out int size);
    [DllImport("advapi32.dll")] private static extern bool GetSecurityDescriptorDacl(nint sd, out bool present, out nint dacl, out bool defaulted);
    [DllImport("advapi32.dll")] private static extern bool GetSecurityDescriptorSacl(nint sd, out bool present, out nint sacl, out bool defaulted);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint mem);
}
