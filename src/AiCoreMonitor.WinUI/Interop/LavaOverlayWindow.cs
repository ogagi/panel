using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AiCoreMonitor.WinUI.Interop;

internal sealed class LavaOverlayWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcAlpha = 0x01;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);
    private static readonly Drip[] Drips =
    [
        new(0.075f, 0.00f, 0.041f, 5.5f, 0.72f, 0.72f),
        new(0.23f, 0.38f, 0.052f, 4.0f, 0.38f, 0.62f),
        new(0.47f, 0.73f, 0.038f, 6.5f, 0.94f, 0.78f),
        new(0.68f, 0.20f, 0.048f, 4.5f, 0.52f, 0.66f),
        new(0.91f, 0.55f, 0.036f, 5.8f, 0.86f, 0.75f)
    ];
    private static readonly Icicle[] Icicles =
    [
        new(0.07f, 0.42f, 7f),
        new(0.19f, 0.68f, 9f),
        new(0.34f, 0.34f, 6f),
        new(0.49f, 0.86f, 10f),
        new(0.64f, 0.50f, 7f),
        new(0.79f, 0.73f, 9f),
        new(0.93f, 0.39f, 6f)
    ];

    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly WindowProcedure _windowProcedure;
    private readonly string _className = $"AiCoreMonitor.Lava.{Guid.NewGuid():N}";
    private readonly System.Threading.Timer _timer;
    private nint _window;
    private int _x;
    private int _y;
    private int _width;
    private int _height;
    private int _panelHeight;
    private bool _enabled = true;
    private bool _frozen;
    private float _amount = 0.78f;
    private bool _disposed;

    public LavaOverlayWindow()
    {
        _windowProcedure = WindowProc;
        var module = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Instance = module,
            Procedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            ClassName = _className
        };
        if (RegisterClassEx(ref windowClass) == 0)
            throw new InvalidOperationException("Could not register the lava overlay window class.");

        _window = CreateWindowEx(WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate,
            _className, string.Empty, WsPopup, 0, 0, 1, 1, 0, 0, module, 0);
        if (_window == 0)
            throw new InvalidOperationException("Could not create the lava overlay window.");

        _timer = new System.Threading.Timer(RenderFrame, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(33));
    }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (_window != 0) ShowWindow(_window, value ? 8 : 0);
        }
    }

    public float Amount
    {
        get => _amount;
        set => _amount = Math.Clamp(value, 0.1f, 1f);
    }

    public bool IsFrozen
    {
        get => _frozen;
        set => _frozen = value;
    }

    public void UpdateBounds(int x, int y, int width, int height, int panelHeight, bool topmost)
    {
        lock (_sync)
        {
            _x = x;
            _y = y;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _panelHeight = Math.Clamp(panelHeight, 1, _height);
            if (_window != 0)
                _ = SetWindowPos(_window, topmost ? HwndTopmost : HwndNotTopmost,
                    _x, _y, _width, _height, SwpNoActivate | SwpShowWindow);
        }
    }

    private void RenderFrame(object? state)
    {
        if (!_enabled || _disposed) return;
        lock (_sync)
        {
            if (_window == 0 || _width < 2 || _height < 8) return;
            using var bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            if (_frozen)
                DrawIce(graphics, _width, _height, _panelHeight, _amount);
            else
                DrawLava(graphics, _width, _height, _panelHeight, _clock.Elapsed.TotalSeconds, _amount);
            Present(bitmap);
        }
    }

    private static void DrawIce(Graphics graphics, int width, int height, int panelHeight, float amount)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(0, panelHeight - 8);

        using var lip = CreatePoolPath(width, 0.35);
        using var lipGlow = new Pen(Color.FromArgb(88, 43, 136, 255), 12) { LineJoin = LineJoin.Round };
        graphics.DrawPath(lipGlow, lip);
        using var lipFill = new LinearGradientBrush(new Rectangle(0, -4, width, 30),
            Color.FromArgb(238, 214, 248, 255), Color.FromArgb(224, 65, 116, 244), LinearGradientMode.Vertical);
        graphics.FillPath(lipFill, lip);
        using var lipHighlight = new Pen(Color.FromArgb(232, 230, 251, 255), 1.4f);
        graphics.DrawPath(lipHighlight, lip);

        var runway = Math.Max(0, height - panelHeight + 6);
        if (runway > 12)
        {
            var count = Math.Clamp((int)Math.Ceiling(Icicles.Length * amount), 1, Icicles.Length);
            foreach (var icicle in Icicles.Take(count))
            {
                var length = Math.Min(runway - 3, 14 + runway * icicle.LengthRatio * (0.45f + 0.55f * amount));
                var centerX = width * icicle.X;
                var half = icicle.Width * 0.5f;
                using var path = new GraphicsPath();
                path.StartFigure();
                path.AddBezier(centerX - half, 2, centerX - half * 0.92f, length * 0.28f,
                    centerX - half * 0.30f, length * 0.74f, centerX, length);
                path.AddBezier(centerX, length, centerX + half * 0.28f, length * 0.74f,
                    centerX + half * 0.92f, length * 0.28f, centerX + half, 2);
                path.CloseFigure();

                using var glow = new Pen(Color.FromArgb(74, 45, 111, 255), icicle.Width * 1.25f)
                { LineJoin = LineJoin.Round };
                graphics.DrawPath(glow, path);
                using var fill = new LinearGradientBrush(
                    new RectangleF(centerX - icicle.Width, 0, icicle.Width * 2, Math.Max(1, length)),
                    Color.FromArgb(242, 220, 249, 255), Color.FromArgb(226, 62, 92, 232), LinearGradientMode.Vertical);
                graphics.FillPath(fill, path);
                using var edge = new Pen(Color.FromArgb(224, 121, 181, 255), 1.1f) { LineJoin = LineJoin.Round };
                graphics.DrawPath(edge, path);
                using var highlight = new Pen(Color.FromArgb(215, 239, 253, 255), 0.9f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawLine(highlight, centerX - half * 0.24f, 5, centerX - half * 0.06f, length * 0.72f);
            }
        }

        graphics.Restore(state);
    }

    private static void DrawLava(Graphics graphics, int width, int height, int panelHeight, double time, float amount)
    {
        DrawTopMelt(graphics, width, panelHeight, time, amount);
        if (amount >= 0.35f) DrawSideRivulet(graphics, width, panelHeight, time, right: false);
        if (amount >= 0.70f) DrawSideRivulet(graphics, width, panelHeight, time, right: true);

        var bottom = graphics.Save();
        graphics.TranslateTransform(0, panelHeight - 8);
        using var pool = CreatePoolPath(width, time);
        using var poolGlow = new Pen(Color.FromArgb(72, 255, 60, 0), 14) { LineJoin = LineJoin.Round };
        graphics.DrawPath(poolGlow, pool);
        using var poolBrush = new LinearGradientBrush(new Rectangle(0, -2, width, 30),
            Color.FromArgb(248, 255, 179, 24), Color.FromArgb(235, 190, 23, 10), LinearGradientMode.Vertical);
        graphics.FillPath(poolBrush, pool);
        using var poolHighlight = new Pen(Color.FromArgb(230, 255, 236, 152), 1.6f);
        graphics.DrawPath(poolHighlight, pool);

        DrawPoolBubbles(graphics, width, time);
        graphics.Restore(bottom);

        var runway = Math.Max(24, height - panelHeight + 8);
        var dripCount = (int)Math.Ceiling(Drips.Length * amount);
        foreach (var drip in Drips.Take(dripCount))
        {
            var progress = (float)((time * drip.Speed + drip.Phase) % 1);
            var growth = SmoothStep(Math.Min(1, progress / 0.70f));
            var fade = progress < 0.70f ? Math.Clamp(growth * 1.7f, 0, 1) : Math.Clamp(1 - (progress - 0.70f) / 0.30f, 0, 1);
            var maximumLength = Math.Max(12, (runway - 18) * drip.LengthRatio * (0.3f + 0.7f * amount));
            var length = 8 + growth * maximumLength;
            var centerX = width * drip.X + (float)Math.Sin(time * 0.47 + drip.Phase * 9) * drip.Width * 0.72f;
            var sway = (float)Math.Sin(time * 0.69 + drip.Phase * 5) * drip.Width * 1.65f;
            var pulse = 0.92f + 0.12f * (float)Math.Sin(time * 1.1 + drip.Phase * 8);

            var strandState = graphics.Save();
            graphics.TranslateTransform(0, panelHeight - 3);
            using var path = CreateViscousDrip(centerX, length, drip.Width * pulse, drip.BulbScale, sway, time + drip.Phase * 11);
            using var outerGlow = new Pen(Color.FromArgb((int)(58 * fade), 255, 55, 0), drip.Width * 1.9f)
            { LineJoin = LineJoin.Round };
            using var innerGlow = new Pen(Color.FromArgb((int)(105 * fade), 255, 153, 24), drip.Width * 0.72f)
            { LineJoin = LineJoin.Round };
            graphics.DrawPath(outerGlow, path);
            graphics.DrawPath(innerGlow, path);

            using var fill = CreateLavaBrush(centerX, length, drip.Width, fade);
            graphics.FillPath(fill, path);
            DrawFlowTexture(graphics, path, centerX, length, drip.Width, sway, time + drip.Phase * 11, fade);
            using var edge = new Pen(Color.FromArgb((int)(215 * fade), 117, 12, 5), 1.35f) { LineJoin = LineJoin.Round };
            graphics.DrawPath(edge, path);
            DrawInternalHighlight(graphics, centerX, length, drip.Width, sway, fade);
            DrawBulbLight(graphics, centerX + sway, length, drip.Width * drip.BulbScale, fade);

            if (progress > 0.72f)
                DrawReleasedDrop(graphics, centerX + sway, length, runway, drip, (progress - 0.72f) / 0.28f);
            graphics.Restore(strandState);
        }
    }

    private static void DrawTopMelt(Graphics graphics, int width, int panelHeight, double time, float amount)
    {
        using var sheet = new GraphicsPath();
        sheet.StartFigure();
        sheet.AddLine(-8, -4, width + 8, -4);
        sheet.AddLine(width + 8, 5, width, 6);
        const int segments = 12;
        for (var index = segments; index > 0; index--)
        {
            var x1 = width * index / (float)segments;
            var x0 = width * (index - 1) / (float)segments;
            var midpoint = (x0 + x1) * 0.5f;
            var edgeY = 7 + 2.3f * (float)Math.Sin(time * 0.42 + index * 1.91);
            var foldY = 11 + 3.5f * (float)Math.Sin(time * 0.31 + index * 1.37);
            sheet.AddBezier(x1, edgeY, midpoint + 7, foldY, midpoint - 7, foldY, x0, edgeY);
        }
        sheet.CloseFigure();

        using var sheetGlow = new Pen(Color.FromArgb(64, 255, 65, 0), 10) { LineJoin = LineJoin.Round };
        graphics.DrawPath(sheetGlow, sheet);
        using var sheetFill = CreateSurfaceBrush(new RectangleF(0, 0, width, 22));
        graphics.FillPath(sheetFill, sheet);
        using var sheetEdge = new Pen(Color.FromArgb(220, 255, 226, 124), 1.2f);
        graphics.DrawPath(sheetEdge, sheet);

        var strands = new (float X, float Length, float Width, float Phase)[]
        {
            (0.16f, 0.12f, 4.2f, 0.3f),
            (0.43f, 0.20f, 5.8f, 1.7f),
            (0.69f, 0.15f, 4.6f, 3.2f),
            (0.88f, 0.24f, 6.2f, 4.6f)
        };
        var strandCount = (int)Math.Ceiling(strands.Length * amount);
        foreach (var strand in strands.Take(strandCount))
        {
            var breath = 0.94f + 0.06f * (float)Math.Sin(time * 0.58 + strand.Phase);
            var length = Math.Max(14, panelHeight * strand.Length * breath * (0.3f + 0.7f * amount));
            var centerX = width * strand.X + 2.2f * (float)Math.Sin(time * 0.37 + strand.Phase);
            var sway = strand.Width * 0.9f * (float)Math.Sin(time * 0.29 + strand.Phase);
            using var path = CreateViscousDrip(centerX, length, strand.Width, 0.58f, sway, time * 0.35 + strand.Phase);
            DrawStrand(graphics, path, centerX, length, strand.Width, sway, time + strand.Phase, 0.94f);
        }
    }

    private static void DrawSideRivulet(Graphics graphics, int width, int panelHeight, double time, bool right)
    {
        var side = right ? 1f : -1f;
        var edge = right ? width - 3.5f : 3.5f;
        var wobble1 = side * (3.2f + 1.5f * (float)Math.Sin(time * 0.31 + (right ? 2.1 : 0.4)));
        var wobble2 = -side * (2.4f + 1.2f * (float)Math.Cos(time * 0.27 + (right ? 0.8 : 2.8)));
        var half = 2.5f;
        using var path = new GraphicsPath();
        path.StartFigure();
        path.AddBezier(edge - half, 8, edge + wobble1 - half, panelHeight * 0.28f,
            edge + wobble2 - half, panelHeight * 0.62f, edge + side * 2 - half, panelHeight - 5);
        path.AddBezier(edge + side * 2 + half, panelHeight - 5, edge + wobble2 + half, panelHeight * 0.62f,
            edge + wobble1 + half, panelHeight * 0.28f, edge + half, 8);
        path.CloseFigure();
        using var glow = new Pen(Color.FromArgb(60, 255, 59, 0), 10) { LineJoin = LineJoin.Round };
        graphics.DrawPath(glow, path);
        using var fill = CreateSurfaceBrush(new RectangleF(edge - 8, 0, 16, panelHeight));
        graphics.FillPath(fill, path);
        using var rim = new Pen(Color.FromArgb(205, 255, 211, 104), 0.9f) { LineJoin = LineJoin.Round };
        graphics.DrawPath(rim, path);
    }

    private static LinearGradientBrush CreateSurfaceBrush(RectangleF bounds)
    {
        var brush = new LinearGradientBrush(bounds, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal);
        brush.InterpolationColors = new ColorBlend
        {
            Positions = [0, 0.24f, 0.48f, 0.72f, 1],
            Colors =
            [
                Color.FromArgb(235, 82, 7, 2),
                Color.FromArgb(245, 204, 37, 8),
                Color.FromArgb(250, 255, 190, 30),
                Color.FromArgb(240, 239, 72, 9),
                Color.FromArgb(230, 67, 5, 3)
            ]
        };
        return brush;
    }

    private static void DrawStrand(Graphics graphics, GraphicsPath path, float centerX, float length,
        float width, float sway, double time, float fade)
    {
        using var outerGlow = new Pen(Color.FromArgb((int)(58 * fade), 255, 55, 0), width * 1.9f)
        { LineJoin = LineJoin.Round };
        graphics.DrawPath(outerGlow, path);
        using var fill = CreateLavaBrush(centerX, length, width, fade);
        graphics.FillPath(fill, path);
        DrawFlowTexture(graphics, path, centerX, length, width, sway, time, fade);
        using var edge = new Pen(Color.FromArgb((int)(215 * fade), 117, 12, 5), 1.1f) { LineJoin = LineJoin.Round };
        graphics.DrawPath(edge, path);
        DrawInternalHighlight(graphics, centerX, length, width, sway, fade);
    }

    private static GraphicsPath CreatePoolPath(int width, double time)
    {
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(-12, -8, width + 12, -8);
        path.AddLine(width + 12, 5, width, 6);
        const int segments = 8;
        for (var index = segments; index > 0; index--)
        {
            var x1 = width * index / (float)segments;
            var x0 = width * (index - 1) / (float)segments;
            var mid = (x1 + x0) / 2;
            var y0 = 8 + 4 * (float)Math.Sin(time * 0.75 + index * 1.7);
            var y1 = 8 + 4 * (float)Math.Sin(time * 0.75 + (index - 1) * 1.7);
            var belly = 15 + 5 * (float)Math.Sin(time * 0.52 + index * 2.1);
            path.AddBezier(x1, y0, mid + 8, belly, mid - 8, belly, x0, y1);
        }
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath CreateViscousDrip(float centerX, float length, float width, float bulbScale, float sway, double time)
    {
        var topHalf = width * (1.06f + 0.08f * (float)Math.Sin(time));
        var neckHalf = width * (0.38f + 0.09f * (float)Math.Sin(time * 0.83 + 1.2));
        var bodyHalf = width * (0.50f + 0.10f * (float)Math.Cos(time * 0.61));
        var bulb = width * bulbScale * 0.72f;
        var bend1 = sway * 0.28f;
        var bend2 = sway * 0.72f;
        var tipX = centerX + sway;

        var path = new GraphicsPath();
        path.StartFigure();
        path.AddBezier(centerX - topHalf, 0,
            centerX - topHalf * 0.95f, length * 0.16f,
            centerX + bend1 - neckHalf, length * 0.30f,
            centerX + bend1 - neckHalf, length * 0.43f);
        path.AddBezier(centerX + bend1 - neckHalf, length * 0.43f,
            centerX + bend2 - bodyHalf, length * 0.61f,
            tipX - bulb, length * 0.78f,
            tipX - bulb, length - bulb * 0.62f);
        path.AddBezier(tipX - bulb, length - bulb * 0.62f,
            tipX - bulb, length - bulb * 0.24f,
            tipX - bulb * 0.52f, length,
            tipX, length);
        path.AddBezier(tipX, length,
            tipX + bulb * 0.52f, length,
            tipX + bulb, length - bulb * 0.24f,
            tipX + bulb, length - bulb * 0.62f);
        path.AddBezier(tipX + bulb, length - bulb * 0.62f,
            tipX + bulb, length * 0.78f,
            centerX + bend2 + bodyHalf, length * 0.61f,
            centerX + bend1 + neckHalf, length * 0.43f);
        path.AddBezier(centerX + bend1 + neckHalf, length * 0.43f,
            centerX + bend1 + neckHalf, length * 0.30f,
            centerX + topHalf * 0.95f, length * 0.16f,
            centerX + topHalf, 0);
        path.CloseFigure();
        return path;
    }

    private static LinearGradientBrush CreateLavaBrush(float centerX, float length, float width, float fade)
    {
        var brush = new LinearGradientBrush(new RectangleF(centerX - width * 2, 0, width * 4, Math.Max(1, length)),
            Color.Transparent, Color.Transparent, LinearGradientMode.Vertical);
        brush.InterpolationColors = new ColorBlend
        {
            Positions = [0, 0.22f, 0.64f, 1],
            Colors =
            [
                Color.FromArgb((int)(248 * fade), 255, 229, 128),
                Color.FromArgb((int)(245 * fade), 255, 168, 25),
                Color.FromArgb((int)(238 * fade), 239, 62, 9),
                Color.FromArgb((int)(242 * fade), 151, 14, 5)
            ]
        };
        return brush;
    }

    private static void DrawFlowTexture(Graphics graphics, GraphicsPath body, float centerX, float length,
        float width, float sway, double time, float fade)
    {
        var state = graphics.Save();
        try
        {
            graphics.SetClip(body, CombineMode.Intersect);

            // A transverse light field makes the transparent body read as a rounded volume.
            using var volume = new LinearGradientBrush(
                new PointF(centerX - width * 1.35f, 0),
                new PointF(centerX + width * 1.35f, 0),
                Color.Transparent,
                Color.Transparent)
            {
                InterpolationColors = new ColorBlend
                {
                    Positions = [0, 0.18f, 0.43f, 0.65f, 1],
                    Colors =
                    [
                        Color.FromArgb((int)(190 * fade), 78, 5, 2),
                        Color.FromArgb((int)(50 * fade), 210, 45, 5),
                        Color.FromArgb((int)(165 * fade), 255, 238, 154),
                        Color.FromArgb((int)(45 * fade), 255, 112, 12),
                        Color.FromArgb((int)(195 * fade), 69, 4, 2)
                    ]
                }
            };
            graphics.FillRectangle(volume, centerX - width * 2, 0, width * 4, length + width);

            DrawCausticVein(graphics, centerX, length, width, sway, time, -0.31f,
                Color.FromArgb((int)(170 * fade), 255, 239, 164), Math.Max(1.1f, width * 0.13f));
            DrawCausticVein(graphics, centerX, length, width, sway, time * 0.83 + 2.4, 0.25f,
                Color.FromArgb((int)(110 * fade), 255, 138, 24), Math.Max(1.5f, width * 0.22f));
            DrawCausticVein(graphics, centerX, length, width, sway, time * 0.67 + 4.8, 0.05f,
                Color.FromArgb((int)(135 * fade), 129, 10, 3), Math.Max(1.2f, width * 0.18f));

            for (var index = 0; index < 3; index++)
            {
                var phase = time * (0.72 + index * 0.11) + index * 2.3;
                var y = 15 + (float)((phase % 1.0) * Math.Max(12, length - 24));
                var x = centerX + sway * (y / Math.Max(1, length)) +
                        (float)Math.Sin(phase * 3.7) * width * 0.32f;
                var radius = Math.Max(1.2f, width * (0.07f + index * 0.018f));
                using var bubble = new Pen(Color.FromArgb((int)(130 * fade), 255, 231, 145), 0.9f);
                graphics.DrawEllipse(bubble, x - radius, y - radius, radius * 2, radius * 2);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawCausticVein(Graphics graphics, float centerX, float length, float width,
        float sway, double time, float lateralOffset, Color color, float strokeWidth)
    {
        var wave1 = (float)Math.Sin(time * 1.37 + lateralOffset * 7) * width * 0.26f;
        var wave2 = (float)Math.Cos(time * 1.11 + lateralOffset * 11) * width * 0.33f;
        using var vein = new GraphicsPath();
        vein.AddBezier(
            centerX + width * lateralOffset, 4,
            centerX + width * lateralOffset + wave1, length * 0.28f,
            centerX + sway * 0.58f + width * lateralOffset + wave2, length * 0.58f,
            centerX + sway + width * lateralOffset * 0.35f, length * 0.94f);
        using var pen = new Pen(color, strokeWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawPath(pen, vein);
    }

    private static void DrawInternalHighlight(Graphics graphics, float centerX, float length, float width, float sway, float fade)
    {
        using var highlight = new GraphicsPath();
        highlight.AddBezier(centerX - width * 0.34f, 7,
            centerX - width * 0.42f, length * 0.22f,
            centerX + sway * 0.30f - width * 0.18f, length * 0.48f,
            centerX + sway * 0.72f - width * 0.20f, length * 0.72f);
        using var glow = new Pen(Color.FromArgb((int)(205 * fade), 255, 238, 163), Math.Max(1.2f, width * 0.16f))
        { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawPath(glow, highlight);
    }

    private static void DrawBulbLight(Graphics graphics, float centerX, float length, float radius, float fade)
    {
        radius = Math.Max(3, radius * 0.72f);
        using var ellipse = new GraphicsPath();
        ellipse.AddEllipse(centerX - radius, length - radius * 1.25f, radius * 2, radius * 1.7f);
        using var light = new PathGradientBrush(ellipse)
        {
            CenterPoint = new PointF(centerX - radius * 0.22f, length - radius * 0.76f),
            CenterColor = Color.FromArgb((int)(240 * fade), 255, 240, 170),
            SurroundColors = [Color.FromArgb(0, 197, 32, 6)]
        };
        graphics.FillPath(light, ellipse);
    }

    private static void DrawReleasedDrop(Graphics graphics, float centerX, float sourceY, int height, Drip drip, float release)
    {
        var eased = release * release;
        var y = sourceY + 8 + eased * Math.Max(10, height - sourceY - 18);
        var radius = drip.Width * drip.BulbScale * (0.52f + 0.18f * (1 - release));
        var alpha = (int)(230 * (1 - release * 0.68f));
        using var glow = new SolidBrush(Color.FromArgb(alpha / 3, 255, 53, 0));
        graphics.FillEllipse(glow, centerX - radius * 1.8f, y - radius * 1.8f, radius * 3.6f, radius * 3.6f);
        using var body = new LinearGradientBrush(new RectangleF(centerX - radius, y - radius, radius * 2, radius * 2.4f),
            Color.FromArgb(alpha, 255, 187, 35), Color.FromArgb(alpha, 213, 39, 8), LinearGradientMode.Vertical);
        graphics.FillEllipse(body, centerX - radius, y - radius, radius * 2, radius * 2.4f);
        using var shine = new SolidBrush(Color.FromArgb(alpha, 255, 240, 172));
        graphics.FillEllipse(shine, centerX - radius * 0.36f, y - radius * 0.55f, radius * 0.38f, radius * 0.52f);
    }

    private static void DrawPoolBubbles(Graphics graphics, int width, double time)
    {
        for (var index = 0; index < 7; index++)
        {
            var phase = time * (0.62 + index * 0.035) + index * 1.83;
            var x = width * (0.08f + index * 0.14f) + (float)Math.Sin(phase) * 8;
            var radius = 1.6f + 1.7f * (0.5f + 0.5f * (float)Math.Sin(phase * 1.7));
            using var bubble = new Pen(Color.FromArgb(170, 255, 227, 139), 1);
            graphics.DrawEllipse(bubble, x - radius, 4 - radius, radius * 2, radius * 2);
        }
    }

    private static float SmoothStep(float value) => value * value * (3 - 2 * value);

    private void Present(Bitmap bitmap)
    {
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var handle = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memory, handle);
        try
        {
            var destination = new NativePoint(_x, _y);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(_width, _height);
            var blend = new BlendFunction(0, 0, 255, AcSrcAlpha);
            _ = UpdateLayeredWindow(_window, screen, ref destination, ref size, memory, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            _ = SelectObject(memory, previous);
            _ = DeleteObject(handle);
            _ = DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            if (_window != 0) _ = DestroyWindow(_window);
            _window = 0;
            _ = UnregisterClass(_className, GetModuleHandle(null));
        }
    }

    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam) => DefWindowProc(window, message, wParam, lParam);

    private readonly record struct Drip(float X, float Phase, float Speed, float Width, float LengthRatio, float BulbScale);
    private readonly record struct Icicle(float X, float LengthRatio, float Width);
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height) { public int Width = width; public int Height = height; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction(byte operation, byte flags, byte alpha, byte format)
    {
        public byte Operation = operation;
        public byte Flags = flags;
        public byte Alpha = alpha;
        public byte Format = format;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint deviceContext);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint deviceContext);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint deviceContext);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint deviceContext, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, ref NativePoint destination, ref NativeSize size, nint sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
}
