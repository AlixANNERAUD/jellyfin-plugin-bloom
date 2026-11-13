using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Draws the friend's name at the bottom of a downloaded poster, so a friend's movie is recognizable
/// in every list, on every client (web, TV, mobile): the badge is part of the image itself.
/// Uses the SkiaSharp copy shipped with Jellyfin; any failure leaves the original image untouched.
/// </summary>
public static class PosterBadge
{
    private const int MaxLabelLength = 18;

    private static readonly SKColor Background = new(0, 0, 0, 175);

    private static readonly SKColor Accent = new(0x00, 0xA4, 0xDC);

    private static int _warned;

    /// <summary>
    /// Adds the badge to an image file, in place.
    /// </summary>
    /// <param name="path">Image file (.jpg, .png or .webp).</param>
    /// <param name="label">Text of the badge (friend name).</param>
    /// <param name="logger">Logger.</param>
    /// <returns>Whether the badge was drawn.</returns>
    public static bool TryApply(string path, string label, ILogger logger)
    {
        try
        {
            return Apply(path, label);
        }
        catch (Exception ex)
        {
            // Missing native Skia, unusual image... the badge is cosmetic: keep the original poster.
            if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0)
            {
                logger.LogWarning(ex, "Bloom: could not draw the friend badge on {Path}", path);
            }

            return false;
        }
    }

    /// <summary>
    /// Shortens a label to fit in the badge.
    /// </summary>
    /// <param name="label">Label.</param>
    /// <returns>Shortened label.</returns>
    public static string Shorten(string label)
    {
        var trimmed = string.Join(' ', (label ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var info = new StringInfo(trimmed);
        return info.LengthInTextElements <= MaxLabelLength
            ? trimmed
            : info.SubstringByTextElements(0, MaxLabelLength - 1).TrimEnd() + "…";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Apply(string path, string label)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => (SKEncodedImageFormat?)null
        };
        var text = Shorten(label);
        if (format is null || text.Length == 0)
        {
            return false;
        }

        SKBitmap? bitmap;
        using (var codec = SKCodec.Create(path))
        {
            // Rotated (EXIF) images would get the badge in the wrong corner: leave them alone.
            if (codec is null || codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
            {
                return false;
            }

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            bitmap = new SKBitmap(info);
            if (codec.GetPixels(info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                bitmap.Dispose();
                return false;
            }
        }

        using (bitmap)
        {
            if (bitmap.Width < 64 || bitmap.Height < 64)
            {
                return false;
            }

            Draw(bitmap, text);

            var tmp = path + ".badge";
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(format.Value, 90))
            {
                if (data is null)
                {
                    return false;
                }

                using var output = File.Create(tmp);
                data.SaveTo(output);
            }

            File.Move(tmp, path, true);
            return true;
        }
    }

    private static void Draw(SKBitmap bitmap, string text)
    {
        // Sized from the short side, so it reads the same on posters and on episode thumbnails.
        var unit = Math.Min(bitmap.Width, bitmap.Height);
        var fontSize = unit * 0.07f;
        var margin = unit * 0.06f;
        var padX = fontSize * 0.6f;
        var pillHeight = fontSize * 1.7f;
        var dotRadius = fontSize * 0.27f;
        var gap = fontSize * 0.4f;

        using var typeface = PickTypeface(text);
        using var font = new SKFont(typeface, fontSize) { Edging = SKFontEdging.Antialias, Subpixel = true };
        var textWidth = font.MeasureText(text);
        var pillWidth = Math.Min(padX + (dotRadius * 2) + gap + textWidth + padX, bitmap.Width - (2 * margin));

        using var canvas = new SKCanvas(bitmap);
        using var fill = new SKPaint { IsAntialias = true, Color = Background };
        // Bottom centre: clients crop the sides of thumbnails in some lists, and use the top corners for
        // their own indicators (watched, number of versions).
        var left = (bitmap.Width - pillWidth) / 2;
        var top = bitmap.Height - margin - pillHeight;
        var pill = new SKRect(left, top, left + pillWidth, top + pillHeight);
        canvas.DrawRoundRect(pill, pillHeight / 2, pillHeight / 2, fill);

        fill.Color = Accent;
        canvas.DrawCircle(pill.Left + padX + dotRadius, pill.MidY, dotRadius, fill);

        fill.Color = SKColors.White;
        font.GetFontMetrics(out var metrics);
        var baseline = pill.MidY - ((metrics.Ascent + metrics.Descent) / 2);
        canvas.Save();
        canvas.ClipRect(new SKRect(pill.Left, pill.Top, pill.Right - (padX / 2), pill.Bottom));
        canvas.DrawText(text, pill.Left + padX + (dotRadius * 2) + gap, baseline, SKTextAlign.Left, font, fill);
        canvas.Restore();
    }

    private static SKTypeface PickTypeface(string text)
    {
        var typeface = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold) ?? SKTypeface.Default;

        // Names in scripts the default font lacks (CJK, Arabic...): ask the system for a font that has them.
        var missing = text.EnumerateRunes().FirstOrDefault(r => !System.Text.Rune.IsWhiteSpace(r) && !typeface.ContainsGlyph(r.Value));
        if (missing.Value != 0)
        {
            var fallback = SKFontManager.Default.MatchCharacter(null, SKFontStyle.Bold, null, missing.Value);
            if (fallback is not null)
            {
                typeface.Dispose();
                return fallback;
            }
        }

        return typeface;
    }
}
