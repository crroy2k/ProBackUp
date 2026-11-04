using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ProBackUp.Security;
internal static class SecurityRegression
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string path, string existing, IntPtr security);
    internal static void Check(string root, Action<bool, string> check)
    {
        var fixture = Path.Combine(root, "security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var directory = Path.Combine(fixture, "pinned");
            using (var guard = new DirectoryGuard())
            {
                guard.Create(Path.Combine(directory, "child"));
                bool denied = false;
                try { Directory.Move(directory, directory + "-renamed"); } catch (IOException) { denied = true; } catch (UnauthorizedAccessException) { denied = true; }
                check(denied && Directory.Exists(directory), "Los directorios usados durante la restauración no pueden reemplazarse mientras están protegidos");
            }
            Directory.Move(directory, directory + "-renamed");
            check(Directory.Exists(directory + "-renamed"), "La protección libera sus handles al terminar");
            using var identity = WindowsIdentity.GetCurrent();
            var acl = WindowsFileSecurity.DirectoryAcl([identity.User!]);
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            check(acl.AreAccessRulesProtected && acl.GetOwner(typeof(SecurityIdentifier))!.Value == "S-1-5-32-544" && rules.Count == 3,
                "Los datos protegidos definen propietario Administradores y solo SYSTEM, administradores y propietario autorizado");
            var untrusted = new DirectorySecurity(); untrusted.SetOwner(new SecurityIdentifier("S-1-5-21-111-222-333-999"));
            bool ownerRejected = false;
            try { WindowsFileSecurity.CheckOwner(untrusted, [identity.User!]); } catch (UnauthorizedAccessException) { ownerRejected = true; }
            check(ownerRejected, "La adopción rechaza propietarios ajenos antes de modificar permisos o guardar secretos");
            var broad = WindowsFileSecurity.DirectoryAcl([identity.User!]);
            broad.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.Modify, AccessControlType.Allow));
            bool accessRejected = false;
            try { WindowsFileSecurity.CheckAccess(broad, [identity.User!, WindowsFileSecurity.Administrators, new SecurityIdentifier("S-1-5-18")], false); }
            catch (UnauthorizedAccessException) { accessRejected = true; }
            check(accessRejected, "La adopción rechaza accesos amplios de otras cuentas antes de guardar secretos");
            var inheritedRead = new DirectorySecurity();
            inheritedRead.SetSecurityDescriptorSddlForm("D:(A;OICIIO;GRGX;;;BU)");
            WindowsFileSecurity.CheckAccess(inheritedRead, [identity.User!, WindowsFileSecurity.Administrators, new SecurityIdentifier("S-1-5-18")], true);
            check(true, "La instalación acepta lectura y ejecución genéricas heredadas de Archivos de programa");
            bool genericWriteRejected = false;
            inheritedRead.SetSecurityDescriptorSddlForm("D:(A;OICIIO;GRGX;;;BU)(A;;GW;;;BU)");
            try { WindowsFileSecurity.CheckAccess(inheritedRead, [identity.User!, WindowsFileSecurity.Administrators, new SecurityIdentifier("S-1-5-18")], true); }
            catch (UnauthorizedAccessException) { genericWriteRejected = true; }
            check(genericWriteRejected, "La instalación sigue rechazando escritura genérica de otras cuentas");
            var file = Path.Combine(fixture, "single.txt"); System.IO.File.WriteAllText(file, "fixture");
            using (var handle = WindowsFileSecurity.Open(file, false))
            {
                WindowsFileSecurity.CheckSingleFile(handle);
                var fileAcl = new FileSecurity(); fileAcl.SetOwner(identity.User!); fileAcl.SetAccessRuleProtection(true, false);
                fileAcl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(file).SetAccessControl(fileAcl);
                check(new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) == identity.User,
                    "Se pueden aplicar permisos usando el archivo fijado sin abrir una ventana de reemplazo");
            }
            var link = Path.Combine(fixture, "second.txt");
            if (!CreateHardLink(link, file, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            bool linkRejected = false;
            using (var handle = WindowsFileSecurity.Open(file, false))
                try { WindowsFileSecurity.CheckSingleFile(handle); } catch (IOException) { linkRejected = true; }
            check(linkRejected, "Los archivos con enlaces físicos se rechazan antes de aplicar ACL privilegiadas");
        }
        finally { Directory.Delete(fixture, true); }
    }
    internal static void Run(string report)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProBackUp-security-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var results = new List<string>();
        try
        {
            Check(root, (valid, text) => { if (!valid) throw new IOException("FAIL: " + text); results.Add("PASS: " + text); });
            System.IO.File.WriteAllText(report, string.Join(Environment.NewLine, results) + Environment.NewLine + "ALL SECURITY TESTS PASSED");
        }
        catch (Exception ex) { System.IO.File.WriteAllText(report, ex.ToString()); throw; }
        finally { Directory.Delete(root, true); }
    }
}
