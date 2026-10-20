using System.Drawing.Drawing2D;
namespace ProBackUp;

internal sealed class ResultNotification : Form
{
    internal const string Support = "Llame a T.I. Ext. 233";
    internal bool Success { get; }
    internal string Heading => Success ? "Ejecutado con éxito" : "Error al ejecutar el respaldo";
    internal string Message { get; }
    internal Color StatusColor => Success ? Color.FromArgb(34, 197, 94) : Color.FromArgb(239, 68, 68);
    internal bool AutoDismiss => Success;
    private readonly System.Windows.Forms.Timer dismiss = new() { Interval = 10000 };
    protected override bool ShowWithoutActivation => true;
    public ResultNotification(BackupRecord record, Action openHistory)
    {
        Success = record.Status == "Correcto";
        var jobName = record.JobName.Length > 70 ? record.JobName[..67] + "…" : record.JobName;
        Message = Success ? $"{jobName}\n{record.Files} archivos respaldados correctamente." : $"{Support}\nRespaldo: {jobName}\nRevise el historial para consultar el detalle.";
        Text = "ProBackUp · " + Heading; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(570, 245); BackColor = Color.FromArgb(22, 31, 44); ForeColor = Color.FromArgb(232, 239, 248); Font = new Font("Segoe UI", 11);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var symbol = new StatusSymbol(Success, StatusColor) { Size = new Size(56, 56), Margin = new Padding(0, 4, 16, 0) };
        layout.Controls.Add(symbol, 0, 0); layout.SetRowSpan(symbol, 2);
        layout.Controls.Add(new Label { Text = Heading, ForeColor = StatusColor, Font = new Font("Segoe UI", 17, FontStyle.Bold), AutoSize = true, MaximumSize = new Size(445, 0) }, 1, 0);
        layout.Controls.Add(new Label { Text = Message, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 10) }, 1, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        Button Add(string text, Action action) { var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(29, 41, 57), ForeColor = ForeColor, Padding = new Padding(10, 5, 10, 5) }; button.Click += (_, _) => action(); buttons.Controls.Add(button); return button; }
        Add(Success ? "Cerrar" : "Entendido", Close); Add("Ver historial", () => { openHistory(); Close(); });
        layout.Controls.Add(buttons, 0, 2); layout.SetColumnSpan(buttons, 2); Controls.Add(layout);
        Shown += (_, _) => { var area = Screen.FromPoint(Cursor.Position).WorkingArea; Location = new Point(Math.Max(area.Left, area.Right - Width - 16), Math.Max(area.Top, area.Bottom - Height - 16)); if (AutoDismiss) dismiss.Start(); };
        dismiss.Tick += (_, _) => Close();
    }
    protected override void Dispose(bool disposing) { if (disposing) dismiss.Dispose(); base.Dispose(disposing); }
    private sealed class StatusSymbol(bool success, Color color) : Control
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color); e.Graphics.FillEllipse(fill, 2, 2, 50, 50);
            using var pen = new Pen(Color.White, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (success) e.Graphics.DrawLines(pen, [new Point(14, 28), new Point(23, 37), new Point(40, 18)]);
            else { e.Graphics.DrawLine(pen, 18, 18, 36, 36); e.Graphics.DrawLine(pen, 36, 18, 18, 36); }
        }
    }
}
