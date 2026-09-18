using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GameFarm.Agent.Interop;

/// <summary>
/// Prevents Windows from putting the display/system to sleep while DayZ is running, unattended,
/// in the interactive session.
///
/// Unlike a normal player, nobody is physically touching the VM's mouse/keyboard once the
/// dashboard starts a session — so Windows' own idle timers (display/sleep timeout, screensaver,
/// any "Machine inactivity limit" policy) can fire mid-session exactly the way they would for a
/// human who genuinely walked away. BattlEye's periodic in-session integrity check does not
/// tolerate the interactive desktop going away underneath the running game, and kicks with
/// "Game restart required" — this was observed to happen consistently a few minutes into an
/// agent-launched session, but never when launched and played manually (where real input keeps
/// resetting those same idle timers). See docs/TROUBLESHOOTING.md.
///
/// <see cref="SetThreadExecutionState"/> is a system-wide power-management hint honored
/// regardless of which session/process calls it, so it works correctly even though this agent
/// itself runs in Session 0 — no interactive-session trick (unlike <see
/// cref="InteractiveProcessLauncher"/>) is needed here. It only covers the display/sleep power
/// timers, though, not a screensaver or an inactivity-limit lock policy, which key off actual
/// input activity rather than power state — see docs/TROUBLESHOOTING.md for the corresponding
/// one-time VM configuration needed to fully rule those out.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IdleActivitySuppressor
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(ExecutionState esFlags);

    /// <summary>Call repeatedly (e.g. once per watchdog tick) while DayZ is running -- the flag
    /// only holds until the next call, so it must be refreshed periodically, not set once.</summary>
    public static void KeepSystemAndDisplayAwake() =>
        SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);

    /// <summary>Releases the hint once DayZ is no longer running, so the VM can idle/sleep
    /// normally again between sessions.</summary>
    public static void AllowSystemAndDisplaySleep() =>
        SetThreadExecutionState(ExecutionState.Continuous);
}
