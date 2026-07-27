using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DataTray.Sdk.Branding;

namespace DataTray.App;

/// <summary>
/// Renders a plugin's <see cref="ProviderIcon"/> to an Avalonia image when the host can decode it — raster
/// only (SVG needs an extra renderer). Shared by the connection tree (<c>MainViewModel</c>) and the Plugin
/// Store's Installed list so the same embedded <c>icon.png</c> shows in both places. Returns null when
/// there's no renderable image, leaving the caller to fall back to a glyph / remote icon / generic vector.
/// </summary>
public static class PluginIconRenderer
{
    /// <summary>Decode width for plugin icons — icons render at 16-20px, so this covers ~3x HiDPI with headroom.</summary>
    public const int IconDecodeWidth = 64;

    public static IImage? Render(ProviderIcon? icon)
    {
        if (icon?.ImageData is not { Length: > 0 } bytes || !CanRender(icon.ImageMediaType))
        {
            return null;
        }

        try
        {
            // Decode straight to a small size (icons render at 16-20px, up to ~2x on HiDPI). A brand logo
            // ships at 512px; decoding it full-size and letting the Image control downscale it looks blurry
            // and wastes ~64x the memory. DecodeToWidth downsamples once, high-quality, at load.
            return Bitmap.DecodeToWidth(new MemoryStream(bytes), IconDecodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }

    private static bool CanRender(string? mediaType) =>
        mediaType is not null
        && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase);
}
