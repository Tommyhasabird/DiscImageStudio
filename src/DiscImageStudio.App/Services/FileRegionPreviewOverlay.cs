using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DvdImageSolver;

namespace DiscImageStudio.Services;

/// <summary>Marks occupied filesystem space on preview PNGs only. The image payload and
/// burn streams never pass through this presentation layer.</summary>
internal static class FileRegionPreviewOverlay
{
    internal static (double Inner, double Outer) DvdLayer1Ring(
        DvdStreamingOptions geometry, uint split, HybridIsoImageWriter.LayoutPlan layout)
    {
        uint occupied = layout.DataEndLbaExclusive - layout.FileDataStartLba;
        uint capacity = layout.VolumeSectors - split;
        double length = capacity * 38688.0 * geometry.ChannelBitLengthNm * 1e-6;
        var track = DvdTrackGeometry.Create(geometry.InnerRadiusMm, geometry.OutputOuterRadiusMm,
            length, geometry.PitchLinear, geometry.PitchQuadratic, geometry.PitchCubic);
        // OTP writes L1 from the outer edge, so the final sectors lie at the inner edge.
        return (geometry.InnerRadiusMm, track.RadiusAtArcLength(length * occupied / capacity));
    }

    internal static void ApplyDvdLayer1(string path, double previewOuter,
        (double Inner, double Outer) ring, CancellationToken token)
        => Apply(path, ring.Inner, ring.Outer, 0, 0, dvdPixelCoordinates: true, token,
            dvdLayer1: true, imageOuterRadius: previewOuter);

    internal static void ApplyCd(string path, CdDiscParameters actual,
        uint drawingStartLba, uint drawingEndLbaExclusive, CancellationToken token = default)
    {
        actual.Validate();
        ValidateRange(checked((uint)actual.OutputSectors), drawingStartLba, drawingEndLbaExclusive);
        Apply(path, actual.InnerRadiusMm, actual.OutputOuterRadiusMm,
            actual.RadiusFromByte((long)drawingStartLba * CdDiscParameters.BytesPerSector),
            actual.RadiusFromByte((long)drawingEndLbaExclusive * CdDiscParameters.BytesPerSector),
            dvdPixelCoordinates: false, token, actual.PreviewImageOuterRadiusMm);
    }

    internal static void ApplyDvd(string path, DvdStreamingOptions actual,
        uint drawingStartLba, uint drawingEndLbaExclusive, CancellationToken token = default)
    {
        actual.Validate();
        ValidateRange(actual.OutputSectors, drawingStartLba, drawingEndLbaExclusive);
        double length = actual.TotalSectors * 38688.0 * actual.ChannelBitLengthNm * 1e-6;
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(actual.InnerRadiusMm,
            actual.OuterRadiusMm, length, actual.PitchLinear, actual.PitchQuadratic, actual.PitchCubic);
        Apply(path, actual.InnerRadiusMm, actual.OutputOuterRadiusMm,
            geometry.RadiusAtArcLength(length * ((double)drawingStartLba / actual.TotalSectors)),
            geometry.RadiusAtArcLength(length * ((double)drawingEndLbaExclusive / actual.TotalSectors)),
            dvdPixelCoordinates: true, token);
    }

    internal static void ApplyBluRay(string path, BluRayDiscParameters actual,
        uint drawingStartSector, uint drawingEndSectorExclusive, CancellationToken token = default)
    {
        actual.Validate();
        ValidateRange(actual.OutputSectors, drawingStartSector, drawingEndSectorExclusive);
        BluRayTrackGeometry geometry = new(actual);
        Apply(path, actual.InnerRadiusMm, actual.OutputOuterRadiusMm,
            geometry.AtSector(drawingStartSector).RadiusMm,
            geometry.AtSector(drawingEndSectorExclusive).RadiusMm,
            dvdPixelCoordinates: false, token);
    }

    private static void ValidateRange(uint total, uint start, uint end)
    {
        if (start >= end || end > total)
            throw new ArgumentOutOfRangeException(nameof(start), "Preview drawing range must lie inside the disc.");
    }

    private static void Apply(string path, double inner, double outer,
        double drawingInner, double drawingOuter, bool dvdPixelCoordinates, CancellationToken token,
        double? cdImageOuterRadius = null, bool dvdLayer1 = false, double? imageOuterRadius = null)
    {
        token.ThrowIfCancellationRequested();
        BitmapSource source;
        using (FileStream input = File.OpenRead(path))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(input,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
        }
        if (source.PixelWidth != source.PixelHeight)
            throw new ArgumentException("Disc previews must be square.", nameof(path));
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int size = source.PixelWidth;
        int stride = checked(size * 4);
        byte[] pixels = new byte[checked(stride * size)];
        source.CopyPixels(pixels, stride, 0);
        double centre = cdImageOuterRadius.HasValue ? size / 2.0 : (size - 1) / 2.0;
        double mmPerPixel = (imageOuterRadius ?? cdImageOuterRadius ?? outer) * 2 / (dvdPixelCoordinates ? size - 1 : size);
        double innerSquared = inner * inner, outerSquared = outer * outer;
        double drawingInnerSquared = drawingInner * drawingInner;
        double drawingOuterSquared = drawingOuter * drawingOuter;
        for (int y = 0; y < size; y++)
        {
            token.ThrowIfCancellationRequested();
            double dy = (y - centre) * mmPerPixel;
            for (int x = 0; x < size; x++)
            {
                double dx = (x - centre) * mmPerPixel;
                double radiusSquared = dx * dx + dy * dy;
                if (radiusSquared < innerSquared || radiusSquared >= outerSquared
                    || radiusSquared >= drawingInnerSquared && radiusSquared < drawingOuterSquared)
                    continue;
                int offset = y * stride + x * 4;
                if (dvdLayer1)
                {
                    double artwork = 0.55 * pixels[offset + 3] / 255;
                    pixels[offset] = (byte)Math.Round(255 + artwork * (pixels[offset] - 255));
                    pixels[offset + 1] = (byte)Math.Round(133 + artwork * (pixels[offset + 1] - 133));
                    pixels[offset + 2] = (byte)Math.Round(51 + artwork * (pixels[offset + 2] - 51));
                    pixels[offset + 3] = 255;
                    continue;
                }
                // Theme blue #3385FF, matching the preview legend and the design guide.
                // File data is deliberately not passed through the black/white classifier.
                pixels[offset] = 0xFF;
                pixels[offset + 1] = 0x85;
                pixels[offset + 2] = 0x33;
                pixels[offset + 3] = 0xFF;
            }
        }
        token.ThrowIfCancellationRequested();
        BitmapSource result = BitmapSource.Create(size, size, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }
}
