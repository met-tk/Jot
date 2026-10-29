using System;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace Journal.Services;

// Redirect the ONE native editor visual through an alpha mask at all times.
// Focus, typing and IME still belong to RichEditBox; no parallel text layout,
// per-keystroke bitmap capture, or focus-dependent renderer switch is involved.
internal sealed class InkWeatheringRenderer : IDisposable
{
    private readonly FrameworkElement _editor;
    private readonly FrameworkElement _host;
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
    private bool _isDisposed;

    public bool HasTexture => !_isDisposed && _texture != null;

    public InkWeatheringRenderer(FrameworkElement editor, FrameworkElement host)
    {
        _editor = editor;
        _host = host;
        var nativeVisual = ElementCompositionPreview.GetElementVisual(editor);
        _compositor = nativeVisual.Compositor;
        _device = CanvasDevice.GetSharedDevice();
        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(_compositor, _device);
        _wearSurface = _graphics.CreateDrawingSurface(new Size(1, 1),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        using (var drawing = CanvasComposition.CreateDrawingSession(_wearSurface))
            drawing.Clear(Microsoft.UI.Colors.Transparent);
        _wearBrush = _compositor.CreateSurfaceBrush(_wearSurface);
        // 必须保持 Fill：当今日日记免老化（_texture == null）时，1x1 透明 _wearSurface 需铺满整个视口，
        // 否则 AlphaMaskEffect 的有效边界会坍缩为左上角 1x1 像素导致编辑器正文被裁切隐藏。
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

        editor.SizeChanged += OnEditorSizeChanged;
        editor.Loaded += OnEditorLoaded;
        editor.Unloaded += OnEditorUnloaded;
    }

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs e) => DrawTexture();

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;
        if (_xamlRoot != null)
        {
            _xamlRoot.Changed -= RootChanged;
        }
        _xamlRoot = _editor.XamlRoot;
        if (_xamlRoot != null)
        {
            _xamlRoot.Changed -= RootChanged;
            _xamlRoot.Changed += RootChanged;
        }
        DrawTexture();
    }

    private void OnEditorUnloaded(object sender, RoutedEventArgs e)
    {
        if (_xamlRoot != null)
        {
            _xamlRoot.Changed -= RootChanged;
            _xamlRoot = null;
        }
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
        if (_isDisposed) return;
        float prevStrength = _strength;
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
        else
        {
            _effect.Properties.InsertScalar("Erosion.Opacity", _strength);
        }

        if (prevStrength <= 0.0001f && _strength > 0.0001f && _texture != null)
        {
            DrawTexture();
        }
    }

    public async Task<CanvasBitmap> LoadTextureAsync(string path)
        => await CanvasBitmap.LoadAsync(_device, path, 96);

    public void SetTexture(CanvasBitmap texture)
    {
        if (_isDisposed)
        {
            texture.Dispose();
            return;
        }
        var old = _texture;
        _texture = texture;
        DrawTexture();
        old?.Dispose();
    }

    public void SetScrollOffset(double offset)
    {
        if (_isDisposed || Math.Abs(_offset - offset) < 0.01) return;
        _offset = offset;
        // 当日日记（免老化，_strength == 0）滚动时无需重绘磨损贴图表面
        if (_strength <= 0.0001f || _texture == null) return;
        DrawTexture();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => DrawTexture();

    private void DrawTexture()
    {
        if (_isDisposed || _texture == null || _editor.ActualWidth <= 0 || _editor.ActualHeight <= 0) return;
        try
        {
            double scale = Math.Max(0.1, _editor.XamlRoot?.RasterizationScale ?? 1.0);
            var size = new Size(
                Math.Max(1, Math.Ceiling(_editor.ActualWidth * scale)),
                Math.Max(1, Math.Ceiling(_editor.ActualHeight * scale)));
            if (_wearSurface.Size != size)
                _wearSurface.Resize(new Windows.Graphics.SizeInt32((int)size.Width, (int)size.Height));
            using var drawing = CanvasComposition.CreateDrawingSession(_wearSurface);
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            drawing.Transform = Matrix3x2.CreateScale((float)scale);
            using var tile = InkWeatheringEffect.CreateTileBrush(_device, _texture, _offset);
            drawing.FillRectangle(new Rect(0, 0, _editor.ActualWidth, _editor.ActualHeight), tile);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InkWeatheringRenderer.DrawTexture] {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _editor.SizeChanged -= OnEditorSizeChanged;
            _editor.Loaded -= OnEditorLoaded;
            _editor.Unloaded -= OnEditorUnloaded;
            if (_xamlRoot != null)
            {
                _xamlRoot.Changed -= RootChanged;
                _xamlRoot = null;
            }
        }
        catch { }

        try { ElementCompositionPreview.SetElementChildVisual(_host, null); } catch { }
        try { _output.Dispose(); } catch { }
        try { _effect.Dispose(); } catch { }
        try { _editorBrush.Dispose(); } catch { }
        try { _editorSurface.Dispose(); } catch { }
        try { _wearBrush.Dispose(); } catch { }
        try { _wearSurface.Dispose(); } catch { }
        try { _texture?.Dispose(); _texture = null; } catch { }
        try { _graphics.Dispose(); } catch { }
    }
}
