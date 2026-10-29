using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.Effects;

namespace Journal.Services;

// Shared by the live editor and pixel-level verification. No backdrop is sampled:
// the output remains transparent wherever the native editor is transparent.
internal static class InkWeatheringEffect
{
    public static AlphaMaskEffect Create(IGraphicsEffectSource editor, IGraphicsEffectSource wear, bool dark,
        float strength = 0)
    {
        var inkGate = new ColorMatrixEffect
        {
            Source = editor,
            AlphaMode = CanvasAlphaMode.Straight,
            ClampOutput = true,
            // Exclude the blue native selection highlight.
            ColorMatrix = dark
                ? new Matrix5x4 { M14 = 6.063f, M24 = 3.576f, M34 = -4.639f, M54 = -0.8f }
                : new Matrix5x4 { M14 = 4.1496f, M24 = -2.8608f, M34 = -5.2888f, M54 = 3f }
        };
        var erosion = new OpacityEffect
        {
            Name = "Erosion",
            Opacity = Math.Clamp(strength, 0, 1),
            Source = new AlphaMaskEffect { Source = wear, AlphaMask = inkGate }
        };
        var retainedInk = new ColorMatrixEffect
        {
            Source = erosion,
            AlphaMode = CanvasAlphaMode.Premultiplied,
            ClampOutput = true,
            ColorMatrix = new Matrix5x4 { M44 = -1, M54 = 1 }
        };
        // A_out = A_editor * (1 - strength * wear * inkGate).
        return new AlphaMaskEffect { Source = editor, AlphaMask = retainedInk };
    }

    public static double GetVerticalPeriod(CanvasBitmap texture)
    {
        float scale = Math.Min(1f, 256f / (float)texture.Size.Width);
        return Math.Max(1.0, texture.Size.Height * scale * 2.0);
    }

    public static CanvasImageBrush CreateTileBrush(CanvasDevice device, CanvasBitmap texture, double offset)
    {
        // A whole-page texture at native resolution produces broad washes. Bring
        // it down to stroke-sized detail, independent of viewport size and DPI.
        float scale = Math.Min(1f, 256f / (float)texture.Size.Width);
        double period = Math.Max(1.0, texture.Size.Height * scale * 2.0);
        double wrapped = ((offset % period) + period) % period;
        return new CanvasImageBrush(device, texture)
        {
            ExtendX = CanvasEdgeBehavior.Mirror,
            ExtendY = CanvasEdgeBehavior.Mirror,
            Interpolation = CanvasImageInterpolation.Linear,
            Transform = Matrix3x2.CreateScale(scale) *
                Matrix3x2.CreateTranslation(0, -(float)wrapped)
        };
    }
}
