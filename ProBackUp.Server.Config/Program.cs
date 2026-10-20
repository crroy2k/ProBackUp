using System.Diagnostics;
using System.Security.Principal;
namespace ProBackUp.Server;
internal static class ConfigProgram
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length == 2 && args[0] == "--self-test") { ConfigTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--preview") { ConfigTests.Preview(args[1]); return 0; }
            if (args.Length > 0) throw new ArgumentException("Argumentos no reconocidos.");
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" }); return 0; }
            using var mutex = new Mutex(true, "Local\\ProBackUpServerConfig", out var first);
            if (!first) { MessageBox.Show("El configurador ya está abierto.", "ProBackUp Server"); return 0; }
            Application.Run(new ConfigForm()); return 0;
        }
        catch (Exception ex)
        {
            if (args.Length == 2) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!); File.WriteAllText(args[1], ex.ToString()); }
            else MessageBox.Show(ConfigOperations.Friendly(ex), "ProBackUp Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
