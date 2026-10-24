using System.Reflection;
using System.Runtime.InteropServices;
using Raylib_cs;

namespace SimNetGateway.Viewer.Frontend;

/// <summary>
/// Opens the raylib window and runs the <see cref="ViewerFrontend"/>. Before that it checks the environment: a display must
/// exist and the native raylib library of the package must load (R-012: linux-x64 and win-x64), so that a machine without them
/// gets a clear message and exit code 2 instead of a crash.
/// </summary>
public sealed class RaylibLauncher : IWindowedLauncher
{
    public string? CheckEnvironment()
    {
        string? display = DisplayCheck.Problem();
        if (display is not null)
        {
            return display;
        }

        try
        {
            if (!NativeLibrary.TryLoad("raylib", typeof(Raylib).Assembly, DllImportSearchPath.AssemblyDirectory, out nint handle))
            {
                return "the native raylib library could not be loaded (expected libraylib.so on linux-x64 or raylib.dll on win-x64 "
                    + "next to the application; platform " + RuntimeInformation.RuntimeIdentifier + ")";
            }

            NativeLibrary.Free(handle);
        }
        catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException or TargetInvocationException)
        {
            return "the native raylib library is unusable: " + ex.Message;
        }

        return null;
    }

    public int Run(ViewerSession session, ViewerCommandLine line)
    {
        try
        {
            using var frontend = new ViewerFrontend(session, line);
            frontend.Run();
            return 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Console.Error.Write("The viewer cannot open a window: " + ex.Message + "\n");
            return 2;
        }
    }
}
