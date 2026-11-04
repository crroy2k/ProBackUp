using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ProBackUp.Security;

internal sealed class DirectoryGuard : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.OrdinalIgnoreCase);
    internal void Hold(string path) => Add(path, false);
    internal void Create(string path) => Add(path, true);
    private void Add(string path, bool create)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Se requiere una ruta absoluta.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        Open(current);
        foreach (var part in full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (handles.ContainsKey(current)) continue;
            if (create) Directory.CreateDirectory(current);
            Open(current);
        }
    }
    private void Open(string path)
    {
        if (handles.ContainsKey(path)) return;
        var handle = WindowsFileSecurity.Open(path, true);
        try
        {
            var info = WindowsFileSecurity.Information(handle);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || (info.Attributes & FileAttributes.Directory) == 0)
                throw new IOException("No se admiten directorios redirigidos: " + path);
            handles.Add(path, handle);
        }
        catch { handle.Dispose(); throw; }
    }
    internal void Release(string path)
    {
        if (handles.Remove(Path.GetFullPath(path), out var handle)) handle.Dispose();
    }
    internal string[] Descendants(string root) => handles.Keys
        .Where(p => p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(p => p.Length).ToArray();
    public void Dispose()
    {
        foreach (var handle in handles.Values.Reverse()) handle.Dispose();
        handles.Clear();
    }
}

internal static class WindowsFileSecurity
{
    internal static readonly SecurityIdentifier Administrators = new("S-1-5-32-544");
    private static readonly SecurityIdentifier System = new("S-1-5-18");
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInformation
    {
        internal FileAttributes Attributes;
        private System.Runtime.InteropServices.ComTypes.FILETIME creation, access, write;
        private uint volume, sizeHigh, sizeLow;
        internal uint Links;
        private uint indexHigh, indexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    internal static SafeFileHandle Open(string path, bool directory)
    {
        // OPEN_REPARSE_POINT inspects the final component itself. Ancestors are held by DirectoryGuard.
        var handle = CreateFile(path, 0x80000000u, directory ? 3u : 1u,
            IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "No se pudo proteger el acceso a " + path); }
        return handle;
    }
    internal static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }
    internal static void CheckSingleFile(SafeFileHandle handle)
    {
        var info = Information(handle);
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Links != 1)
            throw new IOException("No se admiten enlaces ni archivos compartidos mediante enlaces físicos en los datos protegidos.");
    }
    internal static void RequireAdministrator()
    {
        using var user = WindowsIdentity.GetCurrent();
        if (!user.IsSystem && !new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Esta operación requiere administrador.");
    }
    internal static DirectorySecurity DirectoryAcl(IEnumerable<SecurityIdentifier> modifiers, IEnumerable<SecurityIdentifier>? readers = null)
    {
        var acl = new DirectorySecurity();
        acl.SetOwner(Administrators);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { System, Administrators })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var sid in modifiers.Distinct())
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var sid in readers ?? [])
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }
    internal static void CheckOwner(FileSystemSecurity security, IReadOnlyCollection<SecurityIdentifier> trusted)
    {
        var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!;
        if (!trusted.Contains(owner)) throw new UnauthorizedAccessException("Propietario de datos inesperado. T.I. debe revisar la carpeta antes de adoptarla.");
    }
    internal static void CheckAccess(FileSystemSecurity security, IReadOnlyCollection<SecurityIdentifier> trusted, bool publicRead)
    {
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl == null)
            throw new UnauthorizedAccessException("La carpeta tiene permisos sin restricciones. T.I. debe revisarla antes de adoptarla.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || trusted.Contains((SecurityIdentifier)rule.IdentityReference)) continue;
            if (rule.IdentityReference.Value == "S-1-3-0" && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            // Windows retains GENERIC_READ / GENERIC_EXECUTE on inherited ACEs.
            var read = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize | unchecked((FileSystemRights)0xA0000000);
            if (publicRead && (rule.FileSystemRights & ~read) == 0) continue;
            throw new UnauthorizedAccessException("Existen permisos para otras cuentas. T.I. debe revisar la carpeta antes de adoptarla.");
        }
    }
    internal static void SecureTree(string root, IEnumerable<SecurityIdentifier> modifiers, IEnumerable<SecurityIdentifier>? readers = null, IEnumerable<SecurityIdentifier>? previousOwners = null)
    {
        RequireAdministrator();
        var allowed = modifiers.Distinct().ToArray();
        var read = readers?.Distinct().ToArray() ?? [];
        using var identity = WindowsIdentity.GetCurrent();
        var trusted = new List<SecurityIdentifier> { System, Administrators, identity.User! };
        trusted.AddRange(allowed); trusted.AddRange(previousOwners ?? []);
        try { trusted.Add((SecurityIdentifier)new NTAccount(@"NT SERVICE\TrustedInstaller").Translate(typeof(SecurityIdentifier))); }
        catch (IdentityNotMappedException) { }
        using var directories = new DirectoryGuard();
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        directories.Hold(Path.GetDirectoryName(root)!);
        // Create the root with its protected security descriptor in the same OS operation.
        new DirectoryInfo(root).Create(DirectoryAcl(allowed, read));
        directories.Hold(root);
        var pinnedFiles = new List<SafeFileHandle>();
        var entries = new List<(string path, bool directory)>();
        try
        {
            void Inspect(string path, int depth = 0)
            {
                if (depth > 64 || entries.Count >= 100000) throw new IOException("La carpeta de datos supera los límites de revisión de seguridad.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Los datos contienen una redirección: " + path);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory) directories.Hold(path);
                else
                {
                    var file = Open(path, false); pinnedFiles.Add(file); CheckSingleFile(file);
                }
                FileSystemSecurity security = isDirectory ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access) : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                CheckOwner(security, trusted);
                CheckAccess(security, trusted, read.Length > 0);
                entries.Add((path, isDirectory));
                if (isDirectory) foreach (var child in Directory.EnumerateFileSystemEntries(path)) Inspect(child, depth + 1);
            }
            // Validate every entry before changing ACLs or persisting secrets.
            Inspect(Path.GetFullPath(root));
            foreach (var entry in entries)
            {
                if (entry.directory) new DirectoryInfo(entry.path).SetAccessControl(DirectoryAcl(allowed, read));
                else
                {
                    var acl = new FileSecurity(); acl.SetOwner(Administrators); acl.SetAccessRuleProtection(true, false);
                    foreach (var sid in new[] { System, Administrators }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                    foreach (var sid in allowed) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify | FileSystemRights.Synchronize, AccessControlType.Allow));
                    foreach (var sid in read) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                    new FileInfo(entry.path).SetAccessControl(acl);
                }
            }
        }
        finally { foreach (var file in pinnedFiles) file.Dispose(); }
    }
}
