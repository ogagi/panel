using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace AiCoreMonitor.WinUI.Presentation;

internal sealed class LavaCompositionController : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly Compositor _compositor;
    private readonly ContainerVisual _root;
    private readonly List<LavaBlob> _blobs = [];
    private readonly List<CrackLayer> _cracks = [];
    private bool _isEnabled = true;
    private bool _lavaEnabled = true;
    private bool _cracksEnabled = true;
    private float _lavaAmount = 0.78f;
    private float _crackAmount = 0.78f;
    private float _width;
    private float _height;

    public LavaCompositionController(FrameworkElement host)
    {
        _host = host;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _root = _compositor.CreateContainerVisual();
        _root.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, _root);

        AddBlob(Color.FromArgb(66, 48, 196, 255), 0.42f, 0.26f, 0.04f, 0.04f, 13f);
        AddBlob(Color.FromArgb(50, 115, 43, 236), 0.50f, 0.35f, 0.53f, 0.48f, 17f);
        AddBlob(Color.FromArgb(54, 190, 55, 255), 0.45f, 0.28f, 0.78f, 0.12f, 19f);
        AddBlob(Color.FromArgb(42, 56, 24, 148), 0.58f, 0.27f, 0.20f, 0.76f, 23f);

        AddCrack(3.7f, [(0.06f, 0.00f), (0.09f, 0.05f), (0.055f, 0.10f), (0.10f, 0.15f), (0.045f, 0.22f), (0.075f, 0.30f), (0.025f, 0.39f), (0.035f, 0.52f)]);
        AddCrack(4.3f, [(0.27f, 0.00f), (0.25f, 0.06f), (0.29f, 0.11f), (0.24f, 0.17f), (0.28f, 0.23f), (0.23f, 0.31f)]);
        AddCrack(5.1f, [(0.53f, 0.00f), (0.55f, 0.04f), (0.51f, 0.09f), (0.56f, 0.14f), (0.52f, 0.20f), (0.57f, 0.27f)]);
        AddCrack(4.7f, [(0.78f, 0.00f), (0.75f, 0.06f), (0.80f, 0.12f), (0.76f, 0.19f), (0.82f, 0.26f), (0.79f, 0.34f)]);
        AddCrack(3.9f, [(0.96f, 0.00f), (0.93f, 0.07f), (0.97f, 0.14f), (0.92f, 0.23f), (0.96f, 0.32f), (0.94f, 0.44f), (0.98f, 0.57f)]);

        host.SizeChanged += Host_SizeChanged;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            _root.IsVisible = value;
        }
    }

    public bool LavaEnabled
    {
        get => _lavaEnabled;
        set
        {
            _lavaEnabled = value;
            ApplyLavaAmount();
        }
    }

    public bool CracksEnabled
    {
        get => _cracksEnabled;
        set
        {
            _cracksEnabled = value;
            ApplyCrackAmount();
        }
    }

    public float LavaAmount
    {
        get => _lavaAmount;
        set
        {
            _lavaAmount = Math.Clamp(value, 0.1f, 1f);
            ApplyLavaAmount();
        }
    }

    public float CrackAmount
    {
        get => _crackAmount;
        set
        {
            _crackAmount = Math.Clamp(value, 0.1f, 1f);
            ApplyCrackAmount();
        }
    }

    private void ApplyLavaAmount()
    {
        var visibleCount = (int)Math.Ceiling(_blobs.Count * _lavaAmount);
        for (var index = 0; index < _blobs.Count; index++)
            _blobs[index].Visual.IsVisible = _lavaEnabled && index < visibleCount;
    }

    private void ApplyCrackAmount()
    {
        var visibleCount = (int)Math.Ceiling(_cracks.Count * _crackAmount);
        for (var index = 0; index < _cracks.Count; index++)
            _cracks[index].Visual.IsVisible = _cracksEnabled && index < visibleCount;
        if (_width > 0 && _height > 0)
            foreach (var crack in _cracks) RebuildCrack(crack, _width, _height);
    }

    private void AddBlob(Color color, float widthRatio, float heightRatio, float xRatio, float yRatio, float seconds)
    {
        var geometry = _compositor.CreateEllipseGeometry();
        var shape = _compositor.CreateSpriteShape(geometry);
        var brush = _compositor.CreateRadialGradientBrush();
        brush.EllipseCenter = new Vector2(0.5f, 0.5f);
        brush.EllipseRadius = new Vector2(0.5f, 0.5f);
        brush.ColorStops.Insert(0, _compositor.CreateColorGradientStop(0, color));
        brush.ColorStops.Insert(1, _compositor.CreateColorGradientStop(1, Color.FromArgb(0, color.R, color.G, color.B)));
        shape.FillBrush = brush;
        var visual = _compositor.CreateShapeVisual();
        visual.Shapes.Add(shape);
        visual.Opacity = 0.78f;
        _root.Children.InsertAtTop(visual);
        _blobs.Add(new LavaBlob(visual, geometry, widthRatio, heightRatio, xRatio, yRatio, seconds));
    }

    private void AddCrack(float seconds, (float X, float Y)[] points)
    {
        var visual = _compositor.CreateShapeVisual();
        visual.RelativeSizeAdjustment = Vector2.One;
        var pulse = _compositor.CreateScalarKeyFrameAnimation();
        pulse.Duration = TimeSpan.FromSeconds(seconds);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        pulse.InsertKeyFrame(0, 0.12f);
        pulse.InsertKeyFrame(0.42f, 0.62f);
        pulse.InsertKeyFrame(0.50f, 0.22f);
        pulse.InsertKeyFrame(0.58f, 0.78f);
        pulse.InsertKeyFrame(1, 0.12f);
        visual.StartAnimation(nameof(visual.Opacity), pulse);
        _root.Children.InsertAtTop(visual);
        _cracks.Add(new CrackLayer(visual, points));
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Math.Max(1, e.NewSize.Width);
        var height = Math.Max(1, e.NewSize.Height);
        _width = (float)width;
        _height = (float)height;

        foreach (var blob in _blobs)
        {
            var visual = blob.Visual;
            var blobWidth = (float)width * blob.WidthRatio;
            var blobHeight = (float)height * blob.HeightRatio;
            visual.Size = new Vector2(blobWidth, blobHeight);

            blob.Geometry.Center = new Vector2(blobWidth / 2, blobHeight / 2);
            blob.Geometry.Radius = new Vector2(blobWidth / 2, blobHeight / 2);

            var start = new Vector3((float)width * blob.XRatio - blobWidth / 2, (float)height * blob.YRatio - blobHeight / 2, 0);
            var travel = new Vector3((float)width * 0.10f, (float)height * 0.07f, 0);
            var animation = _compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromSeconds(blob.Seconds);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            animation.InsertKeyFrame(0, start);
            animation.InsertKeyFrame(0.5f, start + travel);
            animation.InsertKeyFrame(1, start);
            visual.StartAnimation(nameof(visual.Offset), animation);

            var pulse = _compositor.CreateScalarKeyFrameAnimation();
            pulse.Duration = TimeSpan.FromSeconds(blob.Seconds * 0.43f);
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            pulse.InsertKeyFrame(0, 0.48f);
            pulse.InsertKeyFrame(0.5f, 0.86f);
            pulse.InsertKeyFrame(1, 0.48f);
            visual.StartAnimation(nameof(visual.Opacity), pulse);
        }

        foreach (var crack in _cracks) RebuildCrack(crack, (float)width, (float)height);
    }

    private void RebuildCrack(CrackLayer crack, float width, float height)
    {
        while (crack.Visual.Shapes.Count > 0) crack.Visual.Shapes.RemoveAt(0);
        var glowBrush = _compositor.CreateColorBrush(Color.FromArgb(135, 72, 78, 255));
        var crustBrush = _compositor.CreateColorBrush(Color.FromArgb(230, 21, 10, 67));
        var coreBrush = _compositor.CreateColorBrush(Color.FromArgb(250, 211, 235, 255));
        var segmentCount = Math.Max(1, (int)Math.Ceiling((crack.Points.Length - 1) * (0.3f + 0.7f * _crackAmount)));
        for (var index = 1; index <= segmentCount; index++)
        {
            var start = new Vector2(crack.Points[index - 1].X * width, crack.Points[index - 1].Y * height);
            var end = new Vector2(crack.Points[index].X * width, crack.Points[index].Y * height);
            AddCrackSegment(crack.Visual, start, end, glowBrush, 6.2f);
            AddCrackSegment(crack.Visual, start, end, crustBrush, 3.1f);
            AddCrackSegment(crack.Visual, start, end, coreBrush, 1.35f);

            if (index is 2 or 3)
            {
                var direction = Vector2.Normalize(end - start);
                var normal = new Vector2(-direction.Y, direction.X);
                var branchEnd = end + direction * 12 + normal * (index == 2 ? 15 : -12);
                AddCrackSegment(crack.Visual, end, branchEnd, glowBrush, 3.2f);
                AddCrackSegment(crack.Visual, end, branchEnd, crustBrush, 1.7f);
                AddCrackSegment(crack.Visual, end, branchEnd, coreBrush, 0.8f);
            }
        }
    }

    private void AddCrackSegment(ShapeVisual visual, Vector2 start, Vector2 end, CompositionBrush brush, float thickness)
    {
        var geometry = _compositor.CreateLineGeometry();
        geometry.Start = start;
        geometry.End = end;
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = brush;
        shape.StrokeThickness = thickness;
        visual.Shapes.Add(shape);
    }

    public void Dispose()
    {
        _host.SizeChanged -= Host_SizeChanged;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _root.Dispose();
    }

    private sealed record LavaBlob(ShapeVisual Visual, CompositionEllipseGeometry Geometry,
        float WidthRatio, float HeightRatio, float XRatio, float YRatio, float Seconds);
    private sealed record CrackLayer(ShapeVisual Visual, (float X, float Y)[] Points);
}
