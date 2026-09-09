using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DayZFarm.Agent.Interop;

/// <summary>
/// NOT CURRENTLY USED. Kept for reference/context only -- do not wire this back into
/// SteamManager/WindowsWorkshopManager without re-reading docs/TROUBLESHOOTING.md's BattlEye
/// section first.
///
/// This class was the agent's original solution for launching Steam/DayZ from a LocalSystem
/// Windows Service (Session 0) into the VM's interactive session: it worked (Steam/DayZ
/// genuinely launched and connected), but BattlEye consistently kicked ("Bad Packet"/"Game
/// restart required") every session launched this way, and never a manually-launched one.
/// BattlEye's anti-tamper checks are known to distrust a game process descending from a
/// privileged service using a duplicated security token (CreateProcessAsUser +
/// DuplicateTokenEx, exactly what this class does) -- the same pattern real cheat-injection
/// tooling uses. The fix was to stop needing this pattern at all: the agent now runs as a
/// Scheduled Task directly in the interactive session (see Program.cs and
/// scripts/Install-Agent.ps1), so Steam/DayZ launch via a plain Process.Start, with no token
/// manipulation anywhere in the process tree -- see SteamManager and docs/TROUBLESHOOTING.md.
///
/// Launches a process in the currently logged-on interactive user's desktop session, instead of
/// the caller's own session.
///
/// Necessary because this agent runs as a LocalSystem Windows Service (Session 0,
/// non-interactive — see Program.cs's <c>UseWindowsService</c>), while Steam/DayZ must run in
/// the VM's actual interactive desktop session: only there do they get a real window
/// station/desktop to render into, the real (non-LocalSystem) user's `HKEY_CURRENT_USER` and
/// profile, and — critically — are they the *same* Steam process the logged-on user already has
/// running, rather than a second one.
///
/// A plain `Process.Start(... UseShellExecute: true)` from a Session-0 service does NOT attach
/// to the interactive user's existing Steam instance: it starts a brand-new, isolated `steam.exe`
/// in Session 0 itself, running as LocalSystem with LocalSystem's own (unrelated, logged-out)
/// Steam config. This was observed in practice to be actively harmful, not merely ineffective —
/// Steam's single-instance enforcement does not expect a second instance to appear in a
/// different, non-interactive session, and starting one knocked the real, already-logged-in
/// interactive Steam instance offline instead of leaving it alone, requiring a full VM restart to
/// recover. See docs/TROUBLESHOOTING.md.
///
/// This class uses the standard "service launches a process as the interactive user" Windows
/// pattern: find the active interactive session, borrow that user's primary token via
/// <c>WTSQueryUserToken</c> (only a Session-0 LocalSystem/SYSTEM process may call this), then
/// <c>CreateProcessAsUser</c> with that token and an explicit <c>winsta0\default</c> desktop so
/// the child process actually appears in that user's session, with that user's environment and
/// registry hive.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InteractiveProcessLauncher
{
    public static void StartInInteractiveSession(string fileName, string arguments)
    {
        var sessionId = GetActiveInteractiveSessionId()
            ?? throw new InvalidOperationException(
                "No interactive user session is currently logged on in this VM -- cannot launch Steam/DayZ. " +
                "Log into the VM's desktop (console or RDP) first. See docs/TROUBLESHOOTING.md.");

        if (!WTSQueryUserToken(sessionId, out var userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed.");

        try
        {
            if (!DuplicateTokenEx(userToken, 0, IntPtr.Zero, SecurityImpersonationLevel.SecurityImpersonation,
                    TokenType.TokenPrimary, out var primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");

            try
            {
                var envCreated = CreateEnvironmentBlock(out var envBlock, primaryToken, false);
                try
                {
                    var startupInfo = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFO>(),
                        lpDesktop = @"winsta0\default"
                    };
                    const uint creationFlags = CreateUnicodeEnvironment | CreateNewConsole;
                    var commandLine = $"\"{fileName}\" {arguments}";

                    if (!CreateProcessAsUser(primaryToken, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                            creationFlags, envCreated ? envBlock : IntPtr.Zero, null, ref startupInfo, out var processInfo))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");

                    CloseHandle(processInfo.hProcess);
                    CloseHandle(processInfo.hThread);
                }
                finally
                {
                    if (envCreated) DestroyEnvironmentBlock(envBlock);
                }
            }
            finally
            {
                CloseHandle(primaryToken);
            }
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    private static uint? GetActiveInteractiveSessionId()
    {
        // Prefer WTSEnumerateSessions: correctly finds the connected/active session even for an
        // RDP-based login, where WTSGetActiveConsoleSessionId alone can point at the physical
        // console session instead of the session the administrator actually logged into.
        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessionsPtr, out var count))
        {
            try
            {
                var entrySize = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var session = Marshal.PtrToStructure<WTS_SESSION_INFO>(sessionsPtr + i * entrySize);
                    if (session.State == WtsConnectState.WTSActive)
                        return session.SessionId;
                }
            }
            finally
            {
                WTSFreeMemory(sessionsPtr);
            }
        }

        // Fallback: the console session (covers a plain Hyper-V console/Basic-Session login).
        var consoleSessionId = WTSGetActiveConsoleSessionId();
        return consoleSessionId == 0xFFFFFFFF ? null : consoleSessionId;
    }

    // --- P/Invoke declarations ---

    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewConsole = 0x00000010;

    private enum SecurityImpersonationLevel
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation
    }

    private enum TokenType
    {
        TokenPrimary = 1,
        TokenImpersonation = 2
    }

    private enum WtsConnectState
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public WtsConnectState State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        SecurityImpersonationLevel impersonationLevel, TokenType tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token, string? applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
