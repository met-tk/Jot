using System;
using System.Numerics;
using System.Threading.Tasks;
using Journal.Common;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Journal.Services
{
    /// <summary>
    /// 独立管理全软件全局底色层纸张岁月老化（Global Paper Aging Layer）Composition 合成渲染与资源生命周期
    /// </summary>
    public sealed class GlobalPaperAgingController : IDisposable
    {
        private readonly FrameworkElement _host;
        private readonly FrameworkElement? _rootElement;
        private readonly DispatcherQueue? _dispatcherQueue;
        private Compositor? _agingCompositor;
        private SpriteVisual? _spriteVisual;
        private CompositionEffectBrush? _effectBrush;
        private CompositionSurfaceBrush? _surfaceBrush;
        private CompositionBackdropBrush? _backdropBrush;
        private LoadedImageSurface? _loadedSurface;
        private string? _currentTexturePath;
        private string? _currentBlendKey;
        private int _paperUpdateVersion;
        private bool _isDisposed;

        public GlobalPaperAgingController(FrameworkElement host, FrameworkElement? rootElement, DispatcherQueue? dispatcherQueue)
        {
            _host = host;
            _rootElement = rootElement;
            _dispatcherQueue = dispatcherQueue;
            InitializeVisual();
        }

        private void InitializeVisual()
        {
            try
            {
                if (_rootElement != null)
                {
                    _agingCompositor = ElementCompositionPreview.GetElementVisual(_rootElement).Compositor;
                }
                else
                {
                    _agingCompositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
                }
                if (_agingCompositor == null) return;

                _spriteVisual = _agingCompositor.CreateSpriteVisual();
                _spriteVisual.IsVisible = false;
                _spriteVisual.Opacity = 0.0f;

                var hostVisual = ElementCompositionPreview.GetElementVisual(_host);
                var sizeAnim = _agingCompositor.CreateExpressionAnimation("hostVisual.Size");
                sizeAnim.SetReferenceParameter("hostVisual", hostVisual);
                _spriteVisual.StartAnimation("Size", sizeAnim);

                _host.SizeChanged += OnHostSizeChanged;
                ElementCompositionPreview.SetElementChildVisual(_host, _spriteVisual);

                AgingEngineService.Instance.CurrentViewingDateChanged -= OnCurrentViewingDateChanged;
                AgingEngineService.Instance.CurrentViewingDateChanged += OnCurrentViewingDateChanged;

                AmbianceThemeEngine.AmbiancePaletteChanged -= OnGlobalAmbiancePaletteChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged += OnGlobalAmbiancePaletteChanged;

                Update(forceTextureReload: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalPaperAgingController Init Error] {ex.Message}");
            }
        }

        private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_spriteVisual != null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                _spriteVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            }
        }

        private void OnCurrentViewingDateChanged(object? sender, DateTime date)
        {
            _dispatcherQueue?.TryEnqueue(() => Update(forceTextureReload: false));
        }

        private void OnGlobalAmbiancePaletteChanged()
        {
            _dispatcherQueue?.TryEnqueue(() => Update(forceTextureReload: false));
        }

        public void Update(bool forceTextureReload = false)
        {
            if (_isDisposed) return;
            _ = UpdateAsync(forceTextureReload);
        }

        private async Task UpdateAsync(bool forceTextureReload = false)
        {
            if (_isDisposed || _spriteVisual == null || _agingCompositor == null) return;
            int version = ++_paperUpdateVersion;
            LoadedImageSurface? newSurface = null;
            CompositionSurfaceBrush? newTextureBrush = null;
            CompositionEffectBrush? newEffectBrush = null;
            CompositionBackdropBrush? newBackdropBrush = null;

            try
            {
                DateTime targetDate = AgingEngineService.Instance.CurrentViewingDate;
                var settings = AppSettingsService.Instance.CurrentSettings;
                bool isEnabled = settings.IsAgingEnabled;
                double userStrength = settings.AgingPaperIntensity;
                string? customPath = settings.AgingPaperTexturePath;

                double intensity = AgingEngineService.Instance.CalculateAgingIntensity(targetDate, userStrength, isEnabled);
                if (!isEnabled || intensity <= 0.0001)
                {
                    _spriteVisual.IsVisible = false;
                    _spriteVisual.Opacity = 0.0f;
                    return;
                }

                bool isDark = IsGlobalAgingDarkEffective();
                var (blendMode, invertTexture) = AgingEngineService.ResolvePaperBlendPipeline(settings.AgingPaperBlendMode, isDark);
                string blendKey = $"{blendMode}:{invertTexture}";
                bool pipelineChanged = (_currentBlendKey != blendKey);
                bool textureChanged = (_currentTexturePath != customPath);

                _spriteVisual.Opacity = (float)intensity;
                _spriteVisual.IsVisible = _effectBrush != null;

                if (forceTextureReload || pipelineChanged || textureChanged || _effectBrush == null)
                {
                    string texturePath = await AgingEngineService.Instance.GetPreprocessedPaperTexturePathAsync(customPath, invertTexture);
                    if (_isDisposed || version != _paperUpdateVersion || _spriteVisual == null) return;
                    newSurface = await AgingImageSurfaceLoader.LoadAsync(texturePath);
                    if (_isDisposed || version != _paperUpdateVersion || _spriteVisual == null) return;

                    var blendEffect = new BlendEffect
                    {
                        Mode = blendMode,
                        Background = new CompositionEffectSourceParameter("Background"),
                        Foreground = new CompositionEffectSourceParameter("Foreground")
                    };

                    var effectFactory = _agingCompositor.CreateEffectFactory(blendEffect);
                    newEffectBrush = effectFactory.CreateBrush();

                    newBackdropBrush = _agingCompositor.CreateBackdropBrush();
                    newEffectBrush.SetSourceParameter("Background", newBackdropBrush);

                    newTextureBrush = _agingCompositor.CreateSurfaceBrush(newSurface);
                    newTextureBrush.Stretch = CompositionStretch.UniformToFill;
                    newTextureBrush.HorizontalAlignmentRatio = 0.5f;
                    newTextureBrush.VerticalAlignmentRatio = 0.5f;
                    newEffectBrush.SetSourceParameter("Foreground", newTextureBrush);

                    var oldSurface = _loadedSurface;
                    var oldTextureBrush = _surfaceBrush;
                    var oldEffectBrush = _effectBrush;
                    var oldBackdropBrush = _backdropBrush;
                    _spriteVisual.Brush = newEffectBrush;
                    _effectBrush = newEffectBrush;
                    _backdropBrush = newBackdropBrush;
                    _surfaceBrush = newTextureBrush;
                    _loadedSurface = newSurface;
                    _currentBlendKey = blendKey;
                    _currentTexturePath = customPath;
                    oldTextureBrush?.Dispose();
                    oldEffectBrush?.Dispose();
                    oldBackdropBrush?.Dispose();
                    oldSurface?.Dispose();
                    newSurface = null;
                    newTextureBrush = null;
                    newEffectBrush = null;
                    newBackdropBrush = null;
                }

                if (_spriteVisual != null)
                {
                    _spriteVisual.IsVisible = true;
                    _spriteVisual.Opacity = (float)intensity;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalPaperAgingController.Update Error] {ex.Message}");
            }
            finally
            {
                newTextureBrush?.Dispose();
                newEffectBrush?.Dispose();
                newBackdropBrush?.Dispose();
                newSurface?.Dispose();
            }
        }

        private bool IsGlobalAgingDarkEffective()
        {
            var palette = AmbianceThemeEngine.CurrentPalette;
            if (palette.ForcedDarkTheme.HasValue)
            {
                return palette.ForcedDarkTheme.Value;
            }
            if (_rootElement != null)
            {
                if (_rootElement.ActualTheme == ElementTheme.Dark) return true;
                if (_rootElement.ActualTheme == ElementTheme.Light) return false;
            }
            return Application.Current?.RequestedTheme == ApplicationTheme.Dark;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _paperUpdateVersion++;

            try
            {
                _host.SizeChanged -= OnHostSizeChanged;
                AgingEngineService.Instance.CurrentViewingDateChanged -= OnCurrentViewingDateChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged -= OnGlobalAmbiancePaletteChanged;
            }
            catch { }

            try { ElementCompositionPreview.SetElementChildVisual(_host, null); } catch { }
            try { _surfaceBrush?.Dispose(); _surfaceBrush = null; } catch { }
            try { _effectBrush?.Dispose(); _effectBrush = null; } catch { }
            try { _backdropBrush?.Dispose(); _backdropBrush = null; } catch { }
            try { _loadedSurface?.Dispose(); _loadedSurface = null; } catch { }
            try { _spriteVisual?.Dispose(); _spriteVisual = null; } catch { }
        }
    }
}
