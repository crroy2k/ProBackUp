using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ProBackUp;
public static class MailService
{
    public static void Validate(MailSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Host.Contains("/") || settings.Port is < 1 or > 65535) throw new ArgumentException("Revisa el servidor SMTP y el puerto (1–65535).");
        if (settings.Security is not ("STARTTLS" or "SSL/TLS" or "Sin TLS")) throw new ArgumentException("Selecciona el tipo de seguridad SMTP.");
        if (settings.Security == "Sin TLS" && !string.IsNullOrWhiteSpace(settings.User)) throw new ArgumentException("La autenticación requiere STARTTLS o SSL/TLS.");
        MailboxAddress.Parse(settings.From);
        if (Recipients(settings).Count == 0) throw new ArgumentException("Agrega al menos un destinatario.");
        if (settings.TimeoutSeconds is < 5 or > 120) throw new ArgumentException("El tiempo de espera debe ser de 5 a 120 segundos.");
    }
    private static InternetAddressList Recipients(MailSettings settings) => InternetAddressList.Parse(settings.Recipients.Replace(';', ','));
    public static async Task Send(MailSettings settings, string subject, string body, CancellationToken cancel)
    {
        Validate(settings);
        var message = new MimeMessage(); message.From.Add(MailboxAddress.Parse(settings.From)); message.To.AddRange(Recipients(settings));
        message.Subject = subject; message.Body = new TextPart("plain") { Text = body };
        using var smtp = new SmtpClient { Timeout = settings.TimeoutSeconds * 1000 };
        var mode = settings.Security switch { "SSL/TLS" => SecureSocketOptions.SslOnConnect, "Sin TLS" => SecureSocketOptions.None, _ => SecureSocketOptions.StartTls };
        await smtp.ConnectAsync(settings.Host.Trim(), settings.Port, mode, cancel);
        if (!string.IsNullOrWhiteSpace(settings.User)) await smtp.AuthenticateAsync(settings.User.Trim(), settings.Password(), cancel);
        await smtp.SendAsync(message, cancel); await smtp.DisconnectAsync(true, cancel);
    }
    public static string SafeError(Exception ex, MailSettings settings)
    {
        var message = ex.Message;
        try { var password = settings.Password(); if (password.Length > 0) message = message.Replace(password, "[oculto]", StringComparison.Ordinal); } catch { }
        return message;
    }
}
