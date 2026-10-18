using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Microsoft.Graphics.DirectX;

namespace Journal.Services;

// Redirect the ONE native editor visual through an alpha mask at all times.
// Focus, typing and IME still belong to RichEditBox; no parallel text layout,
// per-keystroke bitmap capture, or focus-dependent renderer switch is involved.
internal sealed class InkWeatheringRenderer
{
    private readonly FrameworkElement _editor;
    private readonly Compositor _compositor;
    private readonly CanvasDevice _device;
    private readonly CompositionGraphicsDevice _graphics;
    private readonly CompositionDrawingSurface _wearSurface;
    private readonly CompositionSurfaceBrush _wearBrush;
    private readonly CompositionVisualSurface _editorSurface;
    private readonly CompositionSurfaceBrush _editorBrush;
    private readonly SpriteVisual _output;
    private CompositionEffectBrush _effect;
    private CanvasBitmap? _texture;
    private bool _dark;
    private float _strength;
    private double _offset;
    private XamlRoot? _xamlRoot;

    public bool HasTexture => _texture != null;

    public InkWeatheringRenderer(FrameworkElement editor, FrameworkElement host)
    {
        _editor = editor;
        var nativeVisual = ElementCompositionPreview.GetElementVisual(editor);
        _compositor = nativeVisual.Compositor;
        _device = CanvasDevice.GetSharedDevice();
        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(_compositor, _device);
        _wearSurface = _graphics.CreateDrawingSurface(new Size(1, 1),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        using (var drawing = CanvasComposition.CreateDrawingSession(_wearSurface))
            drawing.Clear(Microsoft.UI.Colors.Transparent);
        _wearBrush = _compositor.CreateSurfaceBrush(_wearSurface);
        _wearBrush.Stretch = CompositionStretch.Fill;

        _editorSurface = _compositor.CreateVisualSurface();
        _editorSurface.SourceVisual = nativeVisual;
        using var sourceSize = _compositor.CreateExpressionAnimation("source.Size");
        sourceSize.SetReferenceParameter("source", nativeVisual);
        _editorSurface.StartAnimation("SourceSize", sourceSize);
        _editorBrush = _compositor.CreateSurfaceBrush(_editorSurface);
        _editorBrush.Stretch = CompositionStretch.Fill;

        _effect = CreateEffect(false);
        _output = _compositor.CreateSpriteVisual();
        _output.RelativeSizeAdjustment = Vector2.One;
        _output.Brush = _effect;
        ElementCompositionPreview.SetElementChildVisual(host, _output);
        // CompositionVisualSurface ignores the source root's opacity. This is
        // the same redirection pattern used by WinUI Gallery's OpacityMaskView.
        nativeVisual.Opacity = 0;

        editor.SizeChanged += (_, _) => DrawTexture();
        editor.Loaded += (_, _) =>
        {
            _xamlRoot = editor.XamlRoot;
            if (_xamlRoot != null) _xamlRoot.Changed += RootChanged;
            DrawTexture();
        };
        editor.Unloaded += (_, _) =>
        {
            if (_xamlRoot != null) _xamlRoot.Changed -= RootChanged;
            _xamlRoot = null;
        };
    }

    private CompositionEffectBrush CreateEffect(bool dark)
    {
        var graph = InkWeatheringEffect.Create(new CompositionEffectSourceParameter("Editor"),
            new CompositionEffectSourceParameter("Wear"), dark);
        using var factory = _compositor.CreateEffectFactory(graph, new[] { "Erosion.Opacity" });
        var brush = factory.CreateBrush();
        brush.SetSourceParameter("Editor", _editorBrush);
        brush.SetSourceParameter("Wear", _wearBrush);
        brush.Properties.InsertScalar("Erosion.Opacity", _strength);
        return brush;
    }

    public void SetAppearance(double strength, bool dark)
    {
        _strength = (float)Math.Clamp(strength, 0, 1);
        if (_dark != dark)
        {
            var next = CreateEffect(dark);
            var old = _effect;
            _effect = next;
            _output.Brush = next;
            _dark = dark;
            old.Dispose();
        }
        else _effect.Properties.InsertScalar("Erosion.Opacity", _strength);
    }

    public async Task<CanvasBitmap> LoadTextureAsync(string path)
        => await CanvasBitmap.LoadAsync(_device, path, 96);

    public void SetTexture(CanvasBitmap texture)
    {
        var old = _texture;
        _texture = texture;
        DrawTexture();
        old?.Dispose();
    }

    public void SetScrollOffset(double offset)
    {
        if (Math.Abs(_offset - offset) < 0.01) return;
        _offset = offset;
        DrawTexture();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => DrawTexture();

    private void DrawTexture()
    {
        if (_texture == null || _editor.ActualWidth <= 0 || _editor.ActualHeight <= 0) return;
        double scale = _editor.XamlRoot?.RasterizationScale ?? 1;
        var size = new Size(Math.Ceiling(_editor.ActualWidth * scale), Math.Ceiling(_editor.ActualHeight * scale));
        if (_wearSurface.Size != size)
            _wearSurface.Resize(new Windows.Graphics.SizeInt32((int)size.Width, (int)size.Height));
        using var drawing = CanvasComposition.CreateDrawingSession(_wearSurface);
        drawing.Clear(Microsoft.UI.Colors.Transparent);
        drawing.Transform = Matrix3x2.CreateScale((float)scale);
        using var tile = InkWeatheringEffect.CreateTileBrush(_device, _texture, _offset);
        drawing.FillRectangle(new Rect(0, 0, _editor.ActualWidth, _editor.ActualHeight), tile);
    }
}
