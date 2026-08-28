using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AiCoreMonitor.WinUI.Interop;

internal sealed class AppIcon : IDisposable
{
    public AppIcon()
    {
        using var bitmap = new Bitmap(128, 128);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var background = new LinearGradientBrush(new Rectangle(8, 8, 112, 112),
            Color.FromArgb(255, 13, 22, 38), Color.FromArgb(255, 38, 18, 61), LinearGradientMode.ForwardDiagonal);
        graphics.FillRoundedRectangle(background, new Rectangle(8, 8, 112, 112), 28);
        using var rim = new Pen(Color.FromArgb(255, 51, 223, 255), 5);
        graphics.DrawRoundedRectangle(rim, new Rectangle(10, 10, 108, 108), 26);

        using var core = new SolidBrush(Color.FromArgb(255, 234, 247, 255));
        using var font = new Font("Bahnschrift", 56, FontStyle.Bold, GraphicsUnit.Pixel);
        var text = "AI";
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, core, (128 - size.Width) / 2, (128 - size.Height) / 2 - 3);
        Handle = bitmap.GetHicon();
    }

    public nint Handle { get; }

    public void Dispose()
    {
        if (Handle != 0) _ = DestroyIcon(Handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedRectangle(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedRectangle(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
