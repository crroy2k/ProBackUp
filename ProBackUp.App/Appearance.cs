namespace ProBackUp;

public sealed record Palette(Color Canvas, Color Surface, Color Sidebar, Color Input, Color Text, Color Muted, Color Border, Color Accent, Color Selection)
{
    public static Palette Dark { get; } = new(Color.FromArgb(14, 20, 30), Color.FromArgb(22, 31, 44), Color.FromArgb(17, 25, 37), Color.FromArgb(29, 41, 57), Color.FromArgb(232, 239, 248), Color.FromArgb(157, 176, 198), Color.FromArgb(48, 64, 83), Color.FromArgb(38, 195, 162), Color.FromArgb(31, 76, 72));
    public static Palette Light { get; } = new(Color.FromArgb(239, 244, 249), Color.White, Color.FromArgb(228, 237, 245), Color.FromArgb(246, 249, 252), Color.FromArgb(24, 41, 61), Color.FromArgb(83, 105, 128), Color.FromArgb(206, 220, 232), Color.FromArgb(0, 116, 98), Color.FromArgb(217, 240, 233));
}
public static class Appearance
{
    public static void Apply(Control root, bool dark)
    {
        var p = dark ? Palette.Dark : Palette.Light;
        Paint(root, p, p.Canvas);
        root.Invalidate(true);
    }
    private static void Paint(Control c, Palette p, Color parent)
    {
        string role = c.Tag as string ?? "";
        var back = role switch { "canvas" => p.Canvas, "sidebar" => p.Sidebar, "surface" => p.Surface, _ => parent };
        c.ForeColor = role == "muted" ? p.Muted : p.Text;
        if (c is DataGridView grid)
        {
            grid.BackgroundColor = p.Surface; grid.GridColor = p.Border;
            grid.ColumnHeadersDefaultCellStyle.BackColor = p.Input; grid.ColumnHeadersDefaultCellStyle.ForeColor = p.Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Input; grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Muted;
            grid.DefaultCellStyle.BackColor = p.Surface; grid.DefaultCellStyle.ForeColor = p.Text;
            grid.DefaultCellStyle.SelectionBackColor = p.Selection; grid.DefaultCellStyle.SelectionForeColor = p.Text;
            grid.AlternatingRowsDefaultCellStyle.BackColor = p.Input;
            foreach (DataGridViewRow row in grid.Rows)
            {
                row.DefaultCellStyle.BackColor = row.Index % 2 == 0 ? p.Surface : p.Input;
                row.DefaultCellStyle.ForeColor = p.Text;
                row.DefaultCellStyle.SelectionBackColor = p.Selection;
                row.DefaultCellStyle.SelectionForeColor = p.Text;
            }
            return;
        }
        if (c is Button b)
        {
            bool accent = role is "primary" or "nav-active";
            b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false;
            b.BackColor = accent ? p.Accent : role == "nav" ? p.Sidebar : p.Input;
            b.ForeColor = accent ? Color.FromArgb(8, 35, 31) : p.Text;
            if (!ReferenceEquals(p, Palette.Dark) && accent) b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = accent ? p.Accent : role == "nav" ? p.Sidebar : p.Border;
            b.FlatAppearance.MouseOverBackColor = accent ? p.Accent : p.Selection;
            b.FlatAppearance.MouseDownBackColor = p.Selection;
            return;
        }
        if (c is TextBoxBase or ListBox or NumericUpDown or ComboBox or DateTimePicker)
        { back = p.Input; }
        if (c is TextBox text) text.BorderStyle = BorderStyle.FixedSingle;
        if (c is NumericUpDown number) number.BorderStyle = BorderStyle.FixedSingle;
        if (c is ThemeComboBox themed) themed.Palette = p;
        if (c is ComboBox combo) { combo.FlatStyle = FlatStyle.Flat; }
        if (c is CheckBox check) { check.FlatStyle = FlatStyle.Flat; check.UseVisualStyleBackColor = false; }
        c.BackColor = back;
        foreach (Control child in c.Controls) Paint(child, p, back);
    }
    public static void MarkMuted(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label && (c.ForeColor == Color.DimGray || c.ForeColor == Color.FromArgb(91, 108, 131))) c.Tag = "muted";
            if (c is Button b && b.BackColor == Color.FromArgb(0, 112, 103)) b.Tag = "primary";
            MarkMuted(c);
        }
    }
}

public sealed class ThemeComboBox : ComboBox
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Palette Palette { get; set; } = Palette.Dark;
    public ThemeComboBox() { DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 24; DropDownStyle = ComboBoxStyle.DropDownList; }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Palette.Selection : Palette.Input);
        e.Graphics.FillRectangle(background, e.Bounds);
        var text = e.Index >= 0 ? GetItemText(Items[e.Index]) : Text;
        TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(e.Bounds, -5, 0), Palette.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
}
