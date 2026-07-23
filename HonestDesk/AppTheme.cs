namespace HonestDesk;

internal static class AppTheme
{
    public static readonly Color Canvas = Color.FromArgb(244, 247, 252);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceAlt = Color.FromArgb(237, 243, 255);
    public static readonly Color Primary = Color.FromArgb(45, 94, 210);
    public static readonly Color PrimaryHover = Color.FromArgb(35, 78, 184);
    public static readonly Color Text = Color.FromArgb(27, 38, 59);
    public static readonly Color Muted = Color.FromArgb(101, 113, 133);
    public static readonly Color Border = Color.FromArgb(218, 225, 238);
    public static readonly Color Success = Color.FromArgb(27, 145, 98);

    public static Font UiFont(float size = 9.5f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static void StylePrimaryButton(Button button)
    {
        StyleButton(button);
        button.BackColor = Primary;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = PrimaryHover;
    }

    public static void StyleSecondaryButton(Button button)
    {
        StyleButton(button);
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = SurfaceAlt;
    }

    public static void StyleInput(Control control)
    {
        control.BackColor = Surface;
        control.ForeColor = Text;
        control.Font = UiFont();
        if (control is TextBox textBox) textBox.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.Cursor = Cursors.Hand;
        button.Font = UiFont(9.5f, FontStyle.Bold);
        button.UseVisualStyleBackColor = false;
    }
}
