using System.Security.Principal;
using System.Text.Json;
namespace ProBackUp;

public sealed class NetworkCredentialsEditor : UserControl
{
    private readonly ThemeComboBox mode = new();
    private readonly TextBox user = new(), password = new() { UseSystemPasswordChar = true };
    private NetworkSettings saved = new();
    private bool loading;
    private readonly bool sessionOnly;
    public NetworkCredentialsEditor(bool serviceContext = true)
    {
        sessionOnly = serviceContext;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1 }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Field(string title, Control control)
        {
            table.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 6, 0, 6) });
            control.Dock = DockStyle.Top; control.Margin = new Padding(0, 0, 0, 8); table.Controls.Add(control);
        }
        if (sessionOnly) mode.Items.Add("Sesión de Windows del usuario de la tarea (sin contraseña)");
        else mode.Items.AddRange([serviceContext ? "Cuenta del servicio (solo rutas locales)" : "Cuenta de esta aplicación (sin nuevas credenciales)", "Usar mi cuenta actual de Windows", "Usar otra cuenta", "Sesión del usuario guardado (sin contraseña)"]);
        Field("Cuenta de acceso a la red", mode); Field(sessionOnly ? @"Usuario de la tarea (DOMINIO\usuario)" : @"Usuario (DOMINIO\usuario o usuario@dominio)", user); if (!sessionOnly) Field("Contraseña de Windows / red", password);
        table.Controls.Add(new Label { Text = sessionOnly ? "Se usa la sesión iniciada de este usuario, incluso con la pantalla bloqueada. No se guarda contraseña ni se usa la sesión de otra persona. Si ha cerrado sesión, se espera a que vuelva a iniciar para ejecutar el último respaldo vencido." : "La sesión del usuario guardado no requiere contraseña. Otra cuenta requiere su contraseña de Windows; vacío conserva la clave de la misma cuenta.", AutoSize = true, MaximumSize = new Size(710, 0), Tag = "muted", Margin = new Padding(0, 4, 0, 8) });
        Controls.Add(table);
        mode.SelectedIndexChanged += (_, _) => { if (sessionOnly) { user.ReadOnly = false; user.Enabled = true; return; } user.ReadOnly = mode.SelectedIndex == 1; user.Enabled = mode.SelectedIndex != 0; password.Enabled = mode.SelectedIndex is 1 or 2; if (!loading && mode.SelectedIndex == 1) user.Text = WindowsIdentity.GetCurrent().Name; };
        LoadSettings(new());
    }
    public void LoadSettings(NetworkSettings settings)
    {
        saved = JsonSerializer.Deserialize<NetworkSettings>(JsonSerializer.Serialize(settings))!;
        loading = true;
        user.Text = saved.User; password.Clear(); password.PlaceholderText = saved.EncryptedPassword.Length > 0 ? "Contraseña guardada" : "";
        mode.SelectedIndex = sessionOnly ? 0 : saved.Mode == "Current" ? 1 : saved.Mode == "Other" ? 2 : saved.Mode == "Session" ? 3 : 0;
        loading = false;
    }
    internal void SuggestPersonalAccount(string owner)
    {
        if (sessionOnly) { if (string.IsNullOrWhiteSpace(user.Text)) user.Text = string.IsNullOrWhiteSpace(owner) ? UserSession.DesktopUser() : owner; return; }
        if (mode.SelectedIndex != 0) return;
        var current = WindowsIdentity.GetCurrent().Name;
        var account = string.IsNullOrWhiteSpace(owner) ? current : owner;
        mode.SelectedIndex = account.Equals(current, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        user.Text = account;
    }
    public NetworkSettings ReadSettings()
    {
        if (sessionOnly || mode.SelectedIndex == 3)
        {
            var account = user.Text.Trim();
            var session = new NetworkSettings { Mode = "Session", User = account, UserSid = account.Equals(saved.User, StringComparison.OrdinalIgnoreCase) && saved.UserSid.Length > 0 ? saved.UserSid : UserSession.Sid(account) };
            NetworkAccess.Validate(session); return session;
        }
        var result = new NetworkSettings { Mode = mode.SelectedIndex == 1 ? "Current" : mode.SelectedIndex == 2 ? "Other" : "Service", User = user.Text.Trim() };
        if (result.Mode == "Service") { result.User = ""; return result; }
        if (result.Mode == saved.Mode && result.User.Equals(saved.User, StringComparison.OrdinalIgnoreCase)) result.EncryptedPassword = saved.EncryptedPassword;
        if (password.Text.Length > 0) result.SetPassword(password.Text);
        NetworkAccess.Validate(result); return result;
    }
}
