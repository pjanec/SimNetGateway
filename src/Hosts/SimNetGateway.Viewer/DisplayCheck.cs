namespace SimNetGateway.Viewer;

/// <summary>
/// The check that runs before the window is opened (DD-005 section 7): without a display raylib cannot open a window and may
/// abort the process, so the viewer says so and exits with code 2 instead. Pure, so it is testable on any machine.
/// </summary>
public static class DisplayCheck
{
    /// <summary>
    /// Null when a display looks available, else the reason it does not. On Linux a window needs <c>DISPLAY</c> (X11) or
    /// <c>WAYLAND_DISPLAY</c>; Windows and macOS always have one.
    /// </summary>
    public static string? Problem(Func<string, string?> getEnvironmentVariable, bool isLinux)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        if (!isLinux)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(getEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(getEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return null;
        }

        return "no display (neither DISPLAY nor WAYLAND_DISPLAY is set)";
    }

    /// <summary><see cref="Problem(Func{string, string?}, bool)"/> for this process.</summary>
    public static string? Problem() => Problem(Environment.GetEnvironmentVariable, OperatingSystem.IsLinux());
}
