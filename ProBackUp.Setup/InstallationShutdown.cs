using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ProBackUp.Setup;

internal static class InstallationShutdown
{
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, IntPtr processes, uint services, IntPtr serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint session, uint flags, IntPtr callback);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    internal static void CloseClient(string target)
    {
        var executable = Path.Combine(target, "ProBackUp.exe");
        if (!File.Exists(executable)) return;
        Check(RmStartSession(out var session, 0, new StringBuilder(33)));
        try
        {
            Check(RmRegisterResources(session, 1, [executable], 0, IntPtr.Zero, 0, IntPtr.Zero));
            // Ask Windows to close only applications using this installed executable.
            // No force flag, so an application can refuse an unsafe shutdown.
            Check(RmShutdown(session, 0, IntPtr.Zero));
        }
        finally { RmEndSession(session); }
    }

    private static void Check(int error)
    {
        if (error != 0)
            throw new IOException("Windows no pudo cerrar ProBackUp para actualizar. Si hay una operación en curso, espera a que termine y vuelve a intentar. " + new Win32Exception(error).Message);
    }
}
