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
    private const int GwlHwndParent = -8;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);
    private static readonly Drip[] Drips =
    [
        // These are intentionally broad: a viscous sheet narrows only at its neck,
        // rather than becoming a set of uniformly thin hanging lines.
        new(0.075f, 0.00f, 0.041f, 12.0f, 0.58f, 1.05f),
        new(0.23f, 0.38f, 0.052f, 9.5f, 0.34f, 0.90f),
        new(0.47f, 0.73f, 0.038f, 15.5f, 0.70f, 1.16f),
        new(0.68f, 0.20f, 0.048f, 10.5f, 0.43f, 0.95f),
        new(0.91f, 0.55f, 0.036f, 13.5f, 0.62f, 1.10f)
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
    private float _amount = 0.78f;
    private float _lavaHue = 275f;
    private float _variation = 1f;
    private bool _disposed;
    private nint _owner;

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

    public float LavaHue
    {
        get => _lavaHue;
        set => _lavaHue = Math.Clamp(value, 0f, 360f);
    }

    public float Variation
    {
        get => _variation;
        set => _variation = Math.Clamp(value, 0.25f, 2f);
    }

    public void UpdateBounds(nint owner, int x, int y, int width, int height, int panelHeight, bool topmost)
    {
        lock (_sync)
        {
            _x = x;
            _y = y;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _panelHeight = Math.Clamp(panelHeight, 1, _height);
            if (_window != 0 && _owner != owner)
            {
                _ = SetWindowLongPtr(_window, GwlHwndParent, owner);
                _owner = owner;
            }
            if (_window != 0)
                _ = SetWindowPos(_window, topmost ? HwndTopmost : HwndNotTopmost,
                    _x, _y, _width, _height, SwpNoActivate);
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
            DrawLava(graphics, _width, _height, _panelHeight, _clock.Elapsed.TotalSeconds, _amount, _lavaHue, _variation);
            Present(bitmap);
        }
    }

    private static void DrawLava(Graphics graphics, int width, int height, int panelHeight, double time, float amount, float hue, float variation)
    {
        time *= variation;
        DrawTopMelt(graphics, width, panelHeight, time, amount, hue, variation);
        // Side flows are deliberately independent from the bottom drops: they remain visible as a frame effect.
        if (amount >= 0.20f) DrawSideRivulet(graphics, width, panelHeight, time, hue, variation, right: false);
        if (amount >= 0.48f) DrawSideRivulet(graphics, width, panelHeight, time, hue, variation, right: true);
        DrawSlidingEdgeDrops(graphics, width, panelHeight, time, amount, hue, variation);

        var bottom = graphics.Save();
        graphics.TranslateTransform(0, panelHeight - 8);
        using var pool = CreatePoolPath(width, time);
        using var poolGlow = new Pen(LavaColor(hue, 0.72f, 0.9f, 38), 10) { LineJoin = LineJoin.Round };
        graphics.DrawPath(poolGlow, pool);
        using var poolBrush = new LinearGradientBrush(new Rectangle(0, -2, width, 38),
            LavaColor(hue - 8, 0.34f, 1f, 158), LavaColor(hue + 28, 0.82f, 0.42f, 116), LinearGradientMode.Vertical);
        graphics.FillPath(poolBrush, pool);
        using var poolHighlight = new Pen(Color.FromArgb(168, 224, 249, 255), 1.1f);
        graphics.DrawPath(poolHighlight, pool);

        DrawPoolBubbles(graphics, width, time);
        graphics.Restore(bottom);

        var runway = Math.Max(24, height - panelHeight + 8);
        var dripCount = (int)Math.Ceiling(Drips.Length * amount);
        foreach (var drip in Drips.Take(dripCount))
        {
            // A full form/stretch/release cycle should be obvious in a few seconds, not tens of seconds.
            var progress = (float)((time * drip.Speed * 4.5 + drip.Phase) % 1);
            var attachedProgress = Math.Min(1, progress / 0.72f);
            var growth = SmoothStep(attachedProgress);
            var fade = progress < 0.72f
                ? Math.Clamp(growth * 1.5f, 0, 1)
                : Math.Clamp(1 - (progress - 0.72f) / 0.14f, 0, 1);
            // A desktop-height filament looks like a rendering defect.  Dense liquid
            // forms weighty lobes close to its source before it separates into a drop.
            var usableRunway = Math.Min(runway - 18, 340f);
            var maximumLength = Math.Max(42, usableRunway * drip.LengthRatio * (0.42f + 0.58f * amount));
            var length = 14 + growth * maximumLength;
            var centerX = width * drip.X + (float)Math.Sin(time * 0.47 + drip.Phase * 9) * drip.Width * 0.72f;
            var sway = (float)Math.Sin(time * 0.69 + drip.Phase * 5) * drip.Width * 1.65f;
            var pulse = 0.92f + 0.12f * (float)Math.Sin(time * 1.1 + drip.Phase * 8);

            var strandState = graphics.Save();
            graphics.TranslateTransform(0, panelHeight - 3);
            using var path = CreateViscousDrip(centerX, length, drip.Width * pulse, drip.BulbScale, sway, time + drip.Phase * 11);
            using var outerGlow = new Pen(LavaColor(hue, 0.76f, 0.9f, (int)(34 * fade)), drip.Width * 1.45f)
            { LineJoin = LineJoin.Round };
            using var innerGlow = new Pen(LavaColor(hue - 12, 0.38f, 1f, (int)(54 * fade)), drip.Width * 0.48f)
            { LineJoin = LineJoin.Round };
            graphics.DrawPath(outerGlow, path);
            graphics.DrawPath(innerGlow, path);

            using var fill = CreateLavaBrush(centerX, length, drip.Width, fade, hue);
            graphics.FillPath(fill, path);
            DrawFlowTexture(graphics, path, centerX, length, drip.Width, sway, time + drip.Phase * 11, fade, hue);
            using var edge = new Pen(LavaColor(hue + 24, 0.88f, 0.30f, (int)(158 * fade)), 1.15f) { LineJoin = LineJoin.Round };
            graphics.DrawPath(edge, path);
            DrawInternalHighlight(graphics, centerX, length, drip.Width, sway, fade);
            DrawBulbLight(graphics, centerX + sway, length, drip.Width * drip.BulbScale, fade);

            if (progress > 0.72f)
                DrawReleasedDrop(graphics, centerX + sway, 14 + maximumLength, runway, drip, (progress - 0.72f) / 0.28f, hue);
            graphics.Restore(strandState);
        }
    }

    private static void DrawTopMelt(Graphics graphics, int width, int panelHeight, double time, float amount, float hue, float variation)
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

        using var sheetGlow = new Pen(LavaColor(hue, 0.75f, 0.9f, 34), 8) { LineJoin = LineJoin.Round };
        graphics.DrawPath(sheetGlow, sheet);
        using var sheetFill = CreateSurfaceBrush(new RectangleF(0, 0, width, 22), hue);
        graphics.FillPath(sheetFill, sheet);
        using var sheetEdge = new Pen(Color.FromArgb(166, 222, 247, 255), 1.05f);
        graphics.DrawPath(sheetEdge, sheet);

        var strands = new (float X, float Length, float Width, float Phase)[]
        {
            (0.16f, 0.12f, 7.8f, 0.3f),
            (0.43f, 0.20f, 10.5f, 1.7f),
            (0.69f, 0.15f, 8.6f, 3.2f),
            (0.88f, 0.24f, 11.4f, 4.6f)
        };
        var strandCount = (int)Math.Ceiling(strands.Length * amount);
        foreach (var strand in strands.Take(strandCount))
        {
            var breath = 0.94f + 0.06f * (float)Math.Sin(time * 0.58 + strand.Phase);
            var length = Math.Max(14, panelHeight * strand.Length * breath * (0.3f + 0.7f * amount) * (0.68f + variation * 0.32f));
            var centerX = width * strand.X + 2.2f * variation * (float)Math.Sin(time * 0.37 + strand.Phase);
            var sway = strand.Width * (0.4f + variation * 0.5f) * (float)Math.Sin(time * 0.29 + strand.Phase);
            using var path = CreateViscousDrip(centerX, length, strand.Width, 0.58f, sway, time * 0.35 + strand.Phase);
            DrawStrand(graphics, path, centerX, length, strand.Width, sway, time + strand.Phase, 0.94f, hue);
        }
    }

    private static void DrawSideRivulet(Graphics graphics, int width, int panelHeight, double time, float hue, float variation, bool right)
    {
        var side = right ? 1f : -1f;
        var edge = right ? width - 3.5f : 3.5f;
        var wobble1 = side * (3.2f + 1.5f * (float)Math.Sin(time * 0.31 + (right ? 2.1 : 0.4)));
        var wobble2 = -side * (2.4f + 1.2f * (float)Math.Cos(time * 0.27 + (right ? 0.8 : 2.8)));
        var half = 2.5f + variation * 1.8f;
        using var path = new GraphicsPath();
        path.StartFigure();
        path.AddBezier(edge - half, 8, edge + wobble1 - half, panelHeight * 0.28f,
            edge + wobble2 - half, panelHeight * 0.62f, edge + side * 2 - half, panelHeight - 5);
        path.AddBezier(edge + side * 2 + half, panelHeight - 5, edge + wobble2 + half, panelHeight * 0.62f,
            edge + wobble1 + half, panelHeight * 0.28f, edge + half, 8);
        path.CloseFigure();
        using var glow = new Pen(LavaColor(hue, 0.75f, 0.9f, 32), 8) { LineJoin = LineJoin.Round };
        graphics.DrawPath(glow, path);
        using var fill = CreateSurfaceBrush(new RectangleF(edge - 10, 0, 20, panelHeight), hue);
        graphics.FillPath(fill, path);
        using var rim = new Pen(Color.FromArgb(150, 220, 247, 255), 0.85f) { LineJoin = LineJoin.Round };
        graphics.DrawPath(rim, path);
    }

    private static void DrawSlidingEdgeDrops(Graphics graphics, int width, int panelHeight, double time,
        float amount, float hue, float variation)
    {
        var count = Math.Max(1, (int)Math.Ceiling(5 * amount));
        for (var index = 0; index < count; index++)
        {
            var right = index % 2 != 0;
            var phase = (float)((time * (0.11 + index * 0.014) + index * 0.217) % 1);
            var fall = phase * phase;
            var radius = 3.2f + (index % 3) * 1.25f + variation * 0.65f;
            var x = right ? width - radius * 0.58f : radius * 0.58f;
            x += (float)Math.Sin(time * 0.7 + index * 2.1) * 1.4f;
            var y = 14 + fall * Math.Max(20, panelHeight - 28);
            var stretch = 1f + phase * 0.9f;
            DrawWaterDrop(graphics, x, y, radius, stretch, 0.92f, hue);

            if (phase > 0.68f)
            {
                var satellitePhase = (phase - 0.68f) / 0.32f;
                DrawWaterDrop(graphics, x + (right ? -1.2f : 1.2f), y - radius * 2.8f,
                    radius * 0.42f, 1.25f, 1 - satellitePhase * 0.55f, hue + 14);
            }
        }
    }

    private static LinearGradientBrush CreateSurfaceBrush(RectangleF bounds, float hue)
    {
        var brush = new LinearGradientBrush(bounds, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal);
        brush.InterpolationColors = new ColorBlend
        {
            Positions = [0, 0.24f, 0.48f, 0.72f, 1],
            Colors =
            [
                LavaColor(hue + 30, 0.92f, 0.24f, 136),
                LavaColor(hue - 12, 0.74f, 0.64f, 148),
                LavaColor(hue - 8, 0.30f, 1f, 176),
                LavaColor(hue + 20, 0.72f, 0.72f, 150),
                LavaColor(hue + 34, 0.92f, 0.20f, 132)
            ]
        };
        return brush;
    }

    private static void DrawStrand(Graphics graphics, GraphicsPath path, float centerX, float length,
        float width, float sway, double time, float fade, float hue)
    {
        using var outerGlow = new Pen(LavaColor(hue, 0.76f, 0.9f, (int)(32 * fade)), width * 1.4f)
        { LineJoin = LineJoin.Round };
        graphics.DrawPath(outerGlow, path);
        using var fill = CreateLavaBrush(centerX, length, width, fade, hue);
        graphics.FillPath(fill, path);
        DrawFlowTexture(graphics, path, centerX, length, width, sway, time, fade, hue);
        using var edge = new Pen(LavaColor(hue + 24, 0.88f, 0.30f, (int)(156 * fade)), 1.05f) { LineJoin = LineJoin.Round };
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
        // Preserve a substantial volume along the whole attached flow.  The former
        // 0.38x neck and 0.50x body collapsed long drips into wire-like strokes.
        var topHalf = width * (1.34f + 0.10f * (float)Math.Sin(time));
        var neckHalf = width * (0.70f + 0.10f * (float)Math.Sin(time * 0.83 + 1.2));
        var bodyHalf = width * (0.88f + 0.12f * (float)Math.Cos(time * 0.61));
        var bulb = width * bulbScale * 1.02f;
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

    private static LinearGradientBrush CreateLavaBrush(float centerX, float length, float width, float fade, float hue)
    {
        var brush = new LinearGradientBrush(new RectangleF(centerX - width * 2, 0, width * 4, Math.Max(1, length)),
            Color.Transparent, Color.Transparent, LinearGradientMode.Vertical);
        brush.InterpolationColors = new ColorBlend
        {
            Positions = [0, 0.22f, 0.64f, 1],
            Colors =
            [
                LavaColor(hue - 8, 0.28f, 1f, (int)(172 * fade)),
                LavaColor(hue - 18, 0.68f, 0.82f, (int)(142 * fade)),
                LavaColor(hue + 24, 0.78f, 0.46f, (int)(126 * fade)),
                LavaColor(hue + 38, 0.72f, 0.70f, (int)(154 * fade))
            ]
        };
        return brush;
    }

    private static Color LavaColor(float hue, float saturation, float value, int alpha)
    {
        hue = (hue % 360 + 360) % 360;
        var chroma = value * saturation;
        var x = chroma * (1 - MathF.Abs((hue / 60f) % 2 - 1));
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0f), < 120 => (x, chroma, 0f), < 180 => (0f, chroma, x),
            < 240 => (0f, x, chroma), < 300 => (x, 0f, chroma), _ => (chroma, 0f, x)
        };
        var m = value - chroma;
        return Color.FromArgb(Math.Clamp(alpha, 0, 255), (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static void DrawFlowTexture(Graphics graphics, GraphicsPath body, float centerX, float length,
        float width, float sway, double time, float fade, float hue)
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
                        LavaColor(hue + 30, 0.96f, 0.16f, (int)(120 * fade)),
                        LavaColor(hue - 14, 0.72f, 0.60f, (int)(30 * fade)),
                        Color.FromArgb((int)(104 * fade), 220, 249, 255),
                        LavaColor(hue + 10, 0.62f, 0.72f, (int)(24 * fade)),
                        LavaColor(hue + 34, 0.96f, 0.14f, (int)(126 * fade))
                    ]
                }
            };
            graphics.FillRectangle(volume, centerX - width * 2, 0, width * 4, length + width);

            DrawCausticVein(graphics, centerX, length, width, sway, time, -0.31f,
                Color.FromArgb((int)(112 * fade), 220, 250, 255), Math.Max(0.8f, width * 0.10f));
            DrawCausticVein(graphics, centerX, length, width, sway, time * 0.83 + 2.4, 0.25f,
                LavaColor(hue - 12, 0.32f, 1f, (int)(72 * fade)), Math.Max(1f, width * 0.14f));
            DrawCausticVein(graphics, centerX, length, width, sway, time * 0.67 + 4.8, 0.05f,
                LavaColor(hue + 28, 0.86f, 0.34f, (int)(84 * fade)), Math.Max(0.9f, width * 0.12f));

            for (var index = 0; index < 3; index++)
            {
                var phase = time * (0.72 + index * 0.11) + index * 2.3;
                var y = 15 + (float)((phase % 1.0) * Math.Max(12, length - 24));
                var x = centerX + sway * (y / Math.Max(1, length)) +
                        (float)Math.Sin(phase * 3.7) * width * 0.32f;
                var radius = Math.Max(1.2f, width * (0.07f + index * 0.018f));
                using var bubble = new Pen(Color.FromArgb((int)(88 * fade), 225, 250, 255), 0.75f);
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
        using var glow = new Pen(Color.FromArgb((int)(148 * fade), 222, 250, 255), Math.Max(0.9f, width * 0.11f))
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
            CenterColor = Color.FromArgb((int)(148 * fade), 220, 249, 255),
            SurroundColors = [Color.FromArgb(0, 106, 52, 238)]
        };
        graphics.FillPath(light, ellipse);
    }

    private static void DrawReleasedDrop(Graphics graphics, float centerX, float sourceY, int height,
        Drip drip, float release, float hue)
    {
        var gravity = release * release;
        var y = sourceY + 6 + gravity * Math.Max(10, height - sourceY - 12);
        var radius = Math.Max(3.2f, drip.Width * drip.BulbScale * (0.82f - release * 0.12f));
        var stretch = 1.55f - release * 0.48f;
        var fade = Math.Clamp(1 - MathF.Max(0, release - 0.82f) / 0.18f, 0, 1);
        DrawWaterDrop(graphics, centerX, y, radius, stretch, fade, hue);

        if (release < 0.28f)
        {
            var neckFade = 1 - release / 0.28f;
            using var neck = new Pen(LavaColor(hue, 0.48f, 0.92f, (int)(175 * neckFade)),
                Math.Max(0.8f, radius * neckFade * 0.34f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(neck, centerX, sourceY - radius * 0.4f, centerX, y - radius * stretch * 0.72f);
        }
    }

    private static void DrawWaterDrop(Graphics graphics, float centerX, float centerY, float radius,
        float stretch, float fade, float hue)
    {
        if (fade <= 0) return;
        using var drop = CreateWaterDropPath(centerX, centerY, radius, stretch);
        using var glow = new Pen(LavaColor(hue, 0.74f, 0.9f, (int)(34 * fade)), radius * 0.8f)
        { LineJoin = LineJoin.Round };
        graphics.DrawPath(glow, drop);

        using var body = new PathGradientBrush(drop)
        {
            CenterPoint = new PointF(centerX - radius * 0.28f, centerY + radius * 0.10f),
            CenterColor = LavaColor(hue - 12, 0.18f, 1f, (int)(172 * fade)),
            SurroundColors = [LavaColor(hue + 24, 0.88f, 0.34f, (int)(148 * fade))]
        };
        graphics.FillPath(body, drop);

        using var rim = new Pen(LavaColor(hue - 8, 0.18f, 1f, (int)(166 * fade)), Math.Max(0.7f, radius * 0.11f));
        graphics.DrawPath(rim, drop);
        using var highlight = new SolidBrush(Color.FromArgb((int)(178 * fade), 238, 252, 255));
        graphics.FillEllipse(highlight, centerX - radius * 0.40f, centerY - radius * 0.18f,
            radius * 0.34f, radius * 0.48f);
        using var bounce = new Pen(Color.FromArgb((int)(105 * fade), 255, 255, 255), Math.Max(0.7f, radius * 0.10f));
        graphics.DrawArc(bounce, centerX - radius * 0.48f, centerY + radius * 0.24f,
            radius * 0.96f, radius * 0.58f, 18, 135);
    }

    private static GraphicsPath CreateWaterDropPath(float centerX, float centerY, float radius, float stretch)
    {
        var height = radius * Math.Max(1.05f, stretch);
        var top = centerY - height;
        var bottom = centerY + height * 0.72f;
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddBezier(centerX, top,
            centerX - radius * 0.18f, top + height * 0.28f,
            centerX - radius, centerY - height * 0.10f,
            centerX - radius, centerY + height * 0.24f);
        path.AddBezier(centerX - radius, centerY + height * 0.24f,
            centerX - radius * 0.88f, bottom,
            centerX - radius * 0.35f, bottom,
            centerX, bottom);
        path.AddBezier(centerX, bottom,
            centerX + radius * 0.35f, bottom,
            centerX + radius * 0.88f, bottom,
            centerX + radius, centerY + height * 0.24f);
        path.AddBezier(centerX + radius, centerY + height * 0.24f,
            centerX + radius, centerY - height * 0.10f,
            centerX + radius * 0.18f, top + height * 0.28f,
            centerX, top);
        path.CloseFigure();
        return path;
    }

    private static void DrawPoolBubbles(Graphics graphics, int width, double time)
    {
        for (var index = 0; index < 7; index++)
        {
            var phase = time * (0.62 + index * 0.035) + index * 1.83;
            var x = width * (0.08f + index * 0.14f) + (float)Math.Sin(phase) * 8;
            var radius = 1.6f + 1.7f * (0.5f + 0.5f * (float)Math.Sin(phase * 1.7));
            using var bubble = new Pen(Color.FromArgb(155, 189, 244, 255), 1);
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
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint deviceContext);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint deviceContext);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint deviceContext);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint deviceContext, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, ref NativePoint destination, ref NativeSize size, nint sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
}
