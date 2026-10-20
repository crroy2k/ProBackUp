using System.Security.Principal;
namespace ProBackUp.Server;
internal static class Bootstrap
{
    internal static string PfxPath(string value)
    {
        var input = value.Trim().Trim('"');
        if (input.Length == 0) throw new ArgumentException("La ruta está vacía. Introduzca la ruta completa de un PFX existente, o Q para cancelar.");
        if (!Path.IsPathFullyQualified(input)) throw new ArgumentException("Use una ruta completa, por ejemplo D:\\Certificados\\probackup.pfx.");
        if (!Path.GetExtension(input).Equals(".pfx", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(input).Equals(".p12", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Se necesita un archivo .pfx o .p12 que incluya la clave privada; un .cer no es suficiente.");
        if (!File.Exists(input)) throw new ArgumentException("No existe el archivo indicado. Si no tiene un PFX, solicite el certificado a la CA interna antes de continuar.");
        Configuration.NoLinks(input); return Path.GetFullPath(input);
    }
    internal static async Task Configure()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Ejecute como administrador.");
        var root = Configuration.DefaultRoot; Configuration.SecureDirectory(root);
        Console.WriteLine("ProBackUp Server · configuración inicial o recuperación local\nUse un certificado HTTPS emitido por su CA y LDAPS con certificado confiable. No hay modos sin cifrar.");
        string Ask(string caption) { Console.Write(caption + ": "); var value = Console.ReadLine(); if (value == null || value.Trim().Equals("Q", StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException(); return value.Trim(); }
        string Required(string caption) { while (true) { var value = Ask(caption); if (value.Length > 0) return value; Console.WriteLine("Este dato es obligatorio. Introduzca un valor o Q para cancelar."); } }
        int Port(string caption) { while (true) { if (int.TryParse(Ask(caption), out var port) && port is >= 1 and <= 65535) return port; Console.WriteLine("Introduzca un puerto entre 1 y 65535, o Q para cancelar."); } }
        string Secret(string caption)
        {
            Console.Write(caption + ": "); var value = new System.Text.StringBuilder();
            while (true) { var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; } else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar); }
            Console.WriteLine(); return value.ToString();
        }
        var settings = new ServerSettings { PublicHost = Required("Nombre DNS del servidor HTTPS"), Port = Port("Puerto HTTPS (ejemplo 8443)") };
        string pfx;
        while (true) { try { pfx = PfxPath(Ask("Ruta del certificado PFX con clave privada (Q cancela)")); break; } catch (ArgumentException ex) { Console.WriteLine(ex.Message); } }
        while (true) { var password = Secret("Contraseña PFX"); if (password.Length < 12) { Console.WriteLine("El PFX debe estar protegido con al menos 12 caracteres. Reintente o cancele con Ctrl+C."); continue; } settings.ProtectedPfxPassword = Configuration.Protect(password); break; }
        using (var certificate = new Configuration(root, settings).Certificate(pfx)) Console.WriteLine("Certificado HTTPS validado.");
        settings.Ldap.Host = Ask("Host DNS del controlador LDAPS"); settings.Ldap.Port = Port("Puerto LDAPS (normalmente 636)"); settings.Ldap.BaseDn = Ask("Base DN (ejemplo DC=empresa,DC=local)"); settings.Ldap.BindDn = Ask("UPN o DN de cuenta de consulta de solo lectura"); settings.Ldap.ProtectedPassword = Configuration.Protect(Secret("Contraseña de consulta LDAP"));
        settings.Ldap.Users.Add(new AllowedUser { Upn = Ask("UPN del administrador inicial"), Role = "Admin" }); settings.Validate();
        if (await new ActiveDirectory().Lookup(settings.Ldap, settings.Ldap.Users[0].Upn) == null) throw new InvalidOperationException("No se validó LDAPS o el administrador.");
        var target = Path.Combine(root, "server.pfx"); Configuration.NoLinks(target); var oldPfx = File.Exists(target) ? File.ReadAllBytes(target) : null;
        try { if (!pfx.Equals(target, StringComparison.OrdinalIgnoreCase)) File.Copy(pfx, target, true); using var certificate = new Configuration(root, settings).Certificate(); Configuration.Write(Path.Combine(root, "server.json"), settings); }
        catch { if (oldPfx != null) File.WriteAllBytes(target, oldPfx); else if (File.Exists(target)) File.Delete(target); throw; }
        Console.WriteLine($"Configuración guardada. Panel: https://{settings.PublicHost}:{settings.Port}/\nLos usuarios adicionales se autorizan desde Configuración LDAP. Reinicie el servicio tras cambiar certificado o puerto.");
    }
}
