using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace AiCoreMonitor.WinUI.Presentation;

internal sealed class SparklineCompositionController : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly Compositor _compositor;
    private readonly ShapeVisual _visual;
    private double[] _values = [];
    private bool _isEnabled = true;

    public SparklineCompositionController(FrameworkElement host)
    {
        _host = host;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _visual = _compositor.CreateShapeVisual();
        _visual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, _visual);
        host.SizeChanged += Host_SizeChanged;
    }

    public void SetValues(double[] values)
    {
        _values = values;
        Rebuild();
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            _visual.IsVisible = value;
            if (value) Rebuild();
        }
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        while (_visual.Shapes.Count > 0) _visual.Shapes.RemoveAt(0);
        if (!_isEnabled || _values.Length < 2 || _host.ActualWidth <= 0 || _host.ActualHeight <= 0) return;

        var brush = _compositor.CreateColorBrush(Color.FromArgb(125, 39, 216, 255));
        var width = (float)_host.ActualWidth;
        var height = (float)_host.ActualHeight;
        for (var index = 1; index < _values.Length; index++)
        {
            var geometry = _compositor.CreateLineGeometry();
            geometry.Start = new Vector2((index - 1) * width / (_values.Length - 1),
                height - (float)Math.Clamp(_values[index - 1], 0, 100) * height / 100);
            geometry.End = new Vector2(index * width / (_values.Length - 1),
                height - (float)Math.Clamp(_values[index], 0, 100) * height / 100);
            var shape = _compositor.CreateSpriteShape(geometry);
            shape.StrokeBrush = brush;
            shape.StrokeThickness = 1.35f;
            _visual.Shapes.Add(shape);
        }
    }

    public void Dispose()
    {
        _host.SizeChanged -= Host_SizeChanged;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _visual.Dispose();
    }
}
