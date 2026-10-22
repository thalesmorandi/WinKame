// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using ImageMagick;
using ImageMagick.Drawing;

namespace WinKame.Core.Engines;

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public sealed record ImageCompressOptions(int Quality = 75, int MaxDimension = 0, int TargetKb = 0, bool ForceJpeg = false);

public sealed record ImageResizeOptions(double Percent = 100, int MaxWidth = 0, int MaxHeight = 0);

public enum RotateOp { Clockwise90, CounterClockwise90, Rotate180, FlipHorizontal, FlipVertical }

public sealed record AdjustOptions(
    double Brightness = 0,
    double Contrast = 0,
    double Saturation = 100,
    double Hue = 100,
    double Sharpen = 0,
    double Blur = 0,
    bool Grayscale = false,
    bool Sepia = false,
    bool AutoLevel = false);

public sealed record BackgroundOptions(
    string Color1 = "#FF8A00",
    string? Color2 = "#E5487A",
    string Aspect = "original",
    double PaddingPct = 10,
    double CornerPct = 4,
    bool Shadow = true);

public sealed record CollageOptions(
    string Layout = "grid",
    int Cell = 600,
    int Spacing = 16,
    int Corner = 0,
    string Background = "#FFFFFF");

public enum RedactMode { Blur, Pixelate, Fill }

public static class ImageEngine
{
    private static readonly Dictionary<string, MagickFormat> FormatByExt = new()
    {
        ["jpg"] = MagickFormat.Jpeg,
        ["png"] = MagickFormat.Png,
        ["webp"] = MagickFormat.WebP,
        ["heic"] = MagickFormat.Heic,
        ["avif"] = MagickFormat.Avif,
        ["tiff"] = MagickFormat.Tiff,
        ["bmp"] = MagickFormat.Bmp,
        ["gif"] = MagickFormat.Gif,
        ["ico"] = MagickFormat.Ico,
        ["pdf"] = MagickFormat.Pdf,
    };

    private static readonly Dictionary<string, bool> WriteSupport = new();

    static ImageEngine()
    {
        Formats.ImageTargetFilter = CanWrite;
    }

    /// <summary>Call once at startup so the target filter is registered.</summary>
    public static void Init() { }

    public static bool CanWrite(string ext)
    {
        ext = Formats.Canonical(ext);
        lock (WriteSupport)
        {
            if (WriteSupport.TryGetValue(ext, out var ok)) return ok;
            ok = FormatByExt.TryGetValue(ext, out var fmt) && MagickFormatInfo.Create(fmt)?.SupportsWriting == true;
            WriteSupport[ext] = ok;
            return ok;
        }
    }

    public static MagickFormat FormatOf(string ext) =>
        FormatByExt.TryGetValue(Formats.Canonical(ext), out var f) ? f : throw new NotSupportedException(T("Formato de imagem não suportado: {0}", ext));

    public static MagickImage Load(string path)
    {
        var ext = Formats.Ext(path);
        MagickImage img = ext == "svg"
            ? new MagickImage(path, new MagickReadSettings { Density = new Density(192), BackgroundColor = MagickColors.Transparent })
            : new MagickImage(path);
        img.AutoOrient();
        return img;
    }

    /// <summary>A downscaled copy for interactive previews.</summary>
    public static MagickImage LoadPreview(string path, int max)
    {
        var img = Load(path);
        if (img.Width > max || img.Height > max) img.Thumbnail((uint)max, (uint)max);
        return img;
    }

    public static byte[] ToPng(IMagickImage<byte> img) => img.ToByteArray(MagickFormat.Png);

    public static (int Width, int Height) Size(string path)
    {
        var info = new MagickImageInfo(path);
        return ((int)info.Width, (int)info.Height);
    }

    // ---------- conversion ----------

    public static string Convert(string input, string target)
    {
        target = Formats.Canonical(target);
        if (target == "pdf")
        {
            var pdf = OutputPaths.For(input, "pdf");
            ToPdf([input], pdf);
            return pdf;
        }

        var outPath = OutputPaths.For(input, target);
        var fmt = FormatOf(target);
        var src = Formats.Canonical(Formats.Ext(input));

        if (src is "gif" or "webp" && target is "gif" or "webp")
        {
            using var frames = new MagickImageCollection(input);
            if (frames.Count > 1)
            {
                frames.Coalesce();
                if (target == "webp") foreach (var f in frames) f.Quality = 90;
                frames.Write(outPath, fmt);
                return outPath;
            }
        }

        using var img = Load(input);
        PrepareFor(img, target);
        img.Write(outPath, fmt);
        return outPath;
    }

    private static void PrepareFor(MagickImage img, string target)
    {
        switch (target)
        {
            case "jpg":
                FlattenOnWhite(img);
                img.Quality = 92;
                break;
            case "webp":
                img.Quality = 90;
                break;
            case "avif":
                img.Quality = 70;
                break;
            case "heic":
                img.Quality = 80;
                break;
            case "ico":
                if (img.Width > 256 || img.Height > 256) img.Resize(256, 256);
                img.Settings.SetDefine(MagickFormat.Ico, "auto-resize", "256,128,64,48,32,16");
                break;
        }
    }

    private static void FlattenOnWhite(MagickImage img)
    {
        if (!img.HasAlpha) return;
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaOption.Remove);
    }

    /// <summary>One PDF page per image, in the given order.</summary>
    public static void ToPdf(IReadOnlyList<string> images, string outPath)
    {
        using var col = new MagickImageCollection();
        foreach (var f in images)
        {
            var img = Load(f);
            FlattenOnWhite(img);
            img.Density = new Density(150, 150, DensityUnit.PixelsPerInch);
            img.Settings.Compression = CompressionMethod.JPEG;
            img.Quality = 90;
            col.Add(img);
        }
        col.Write(outPath, MagickFormat.Pdf);
    }

    // ---------- tools ----------

    /// <summary>Output extension for an edited copy: same format when we can write it, else PNG/JPG.</summary>
    private static string SameOrFallback(string input, bool prefersAlpha)
    {
        var src = Formats.Canonical(Formats.Ext(input));
        if (src is "jpg" or "png" or "webp" or "tiff" or "bmp" or "gif" or "avif" or "heic" && CanWrite(src)) return src;
        return prefersAlpha ? "png" : "jpg";
    }

    public static string Compress(string input, ImageCompressOptions o)
    {
        using var img = Load(input);
        ShrinkToMax(img, o.MaxDimension);
        StripKeepingColor(img);

        var src = Formats.Canonical(Formats.Ext(input));
        var outExt = o.ForceJpeg ? "jpg" : src switch
        {
            "jpg" or "webp" or "avif" or "png" => src,
            "heic" when CanWrite("heic") => "heic",
            _ => "jpg",
        };
        var fmt = FormatOf(outExt);
        if (outExt == "jpg") FlattenOnWhite(img);

        byte[] data;
        long target = o.TargetKb > 0 ? o.TargetKb * 1024L : 0;
        if (outExt == "png")
        {
            img.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
            img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            data = img.ToByteArray(MagickFormat.Png);
            var tries = 0;
            while (target > 0 && data.Length > target && tries++ < 8)
            {
                img.Resize(new Percentage(85));
                data = img.ToByteArray(MagickFormat.Png);
            }
        }
        else if (target > 0)
        {
            data = FitToSize(img, fmt, target);
        }
        else
        {
            img.Quality = (uint)Math.Clamp(o.Quality, 1, 100);
            data = img.ToByteArray(fmt);
        }

        var outPath = OutputPaths.For(input, outExt, "comprimido");
        File.WriteAllBytes(outPath, data);
        return outPath;
    }

    /// <summary>Binary search on quality, then downscale if even the lowest quality is too big.</summary>
    private static byte[] FitToSize(MagickImage img, MagickFormat fmt, long targetBytes)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            int lo = 8, hi = 95;
            byte[]? best = null;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                img.Quality = (uint)mid;
                var bytes = img.ToByteArray(fmt);
                if (bytes.Length <= targetBytes)
                {
                    best = bytes;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            if (best is not null) return best;
            img.Resize(new Percentage(80));
        }
        img.Quality = 8;
        return img.ToByteArray(fmt);
    }

    private static void ShrinkToMax(MagickImage img, int max)
    {
        if (max > 0 && (img.Width > max || img.Height > max)) img.Resize((uint)max, (uint)max);
    }

    private static void StripKeepingColor(MagickImage img)
    {
        var icc = img.GetColorProfile();
        img.Strip();
        if (icc is not null) img.SetProfile(icc);
    }

    public static string Resize(string input, ImageResizeOptions o)
    {
        using var img = Load(input);
        if (o.MaxWidth > 0 || o.MaxHeight > 0)
        {
            img.Resize(new MagickGeometry((uint)Math.Max(0, o.MaxWidth), (uint)Math.Max(0, o.MaxHeight)));
        }
        else if (Math.Abs(o.Percent - 100) > 0.01)
        {
            img.Resize(new Percentage(o.Percent));
        }
        return Save(img, input, "redimensionado", prefersAlpha: true);
    }

    public static double ParseAspect(string aspect)
    {
        var parts = aspect.Split(':', 'x', '/');
        if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var b) && a > 0 && b > 0)
        {
            return a / b;
        }
        return 0;
    }

    /// <summary>The largest centered rectangle with the given width/height ratio.</summary>
    public static PixelRect CenterRect(int width, int height, double ratio)
    {
        if (ratio <= 0) return new PixelRect(0, 0, width, height);
        int w = width, h = (int)Math.Round(width / ratio);
        if (h > height)
        {
            h = height;
            w = (int)Math.Round(height * ratio);
        }
        return new PixelRect((width - w) / 2, (height - h) / 2, w, h);
    }

    public static string Crop(string input, PixelRect? rect, string? aspect, int outWidth = 0, int outHeight = 0)
    {
        using var img = Load(input);
        var r = rect ?? CenterRect((int)img.Width, (int)img.Height, ParseAspect(aspect ?? ""));
        r = Clamp(r, (int)img.Width, (int)img.Height);
        img.Crop(new MagickGeometry(r.X, r.Y, (uint)r.Width, (uint)r.Height));
        img.ResetPage();
        if (outWidth > 0 && outHeight > 0)
        {
            img.Resize(new MagickGeometry((uint)outWidth, (uint)outHeight) { IgnoreAspectRatio = true });
        }
        return Save(img, input, "recortado", prefersAlpha: true);
    }

    private static PixelRect Clamp(PixelRect r, int w, int h)
    {
        var x = Math.Clamp(r.X, 0, Math.Max(0, w - 1));
        var y = Math.Clamp(r.Y, 0, Math.Max(0, h - 1));
        var rw = Math.Clamp(r.Width, 1, w - x);
        var rh = Math.Clamp(r.Height, 1, h - y);
        return new PixelRect(x, y, rw, rh);
    }

    public static string Rotate(string input, RotateOp op)
    {
        using var img = Load(input);
        switch (op)
        {
            case RotateOp.Clockwise90: img.Rotate(90); break;
            case RotateOp.CounterClockwise90: img.Rotate(-90); break;
            case RotateOp.Rotate180: img.Rotate(180); break;
            case RotateOp.FlipHorizontal: img.Flop(); break;
            case RotateOp.FlipVertical: img.Flip(); break;
        }
        return Save(img, input, "girado", prefersAlpha: true);
    }

    public static string StripMetadata(string input)
    {
        using var img = Load(input);
        StripKeepingColor(img);
        return Save(img, input, "sem metadados", prefersAlpha: true);
    }

    public static void ApplyAdjust(MagickImage img, AdjustOptions o)
    {
        if (o.AutoLevel) img.AutoLevel();
        if (Math.Abs(o.Brightness) > 0.01 || Math.Abs(o.Contrast) > 0.01)
        {
            img.BrightnessContrast(new Percentage(o.Brightness), new Percentage(o.Contrast));
        }
        if (Math.Abs(o.Saturation - 100) > 0.01 || Math.Abs(o.Hue - 100) > 0.01)
        {
            img.Modulate(new Percentage(100), new Percentage(o.Saturation), new Percentage(o.Hue));
        }
        if (o.Grayscale) img.Grayscale();
        if (o.Sepia) img.SepiaTone();
        if (o.Blur > 0.01) img.GaussianBlur(0, o.Blur);
        if (o.Sharpen > 0.01) img.Sharpen(0, o.Sharpen);
    }

    public static string Adjust(string input, AdjustOptions o)
    {
        using var img = Load(input);
        ApplyAdjust(img, o);
        return Save(img, input, "editado", prefersAlpha: true);
    }

    public static MagickImage RenderBackground(MagickImage photo, BackgroundOptions o)
    {
        var pw = photo.Width;
        var ph = photo.Height;
        var pad = (uint)Math.Round(Math.Max(pw, ph) * Math.Max(0, o.PaddingPct) / 100.0);
        uint cw = pw + 2 * pad, ch = ph + 2 * pad;
        var ratio = ParseAspect(o.Aspect);
        if (ratio > 0)
        {
            if (cw / (double)ch < ratio) cw = (uint)Math.Round(ch * ratio);
            else ch = (uint)Math.Round(cw / ratio);
        }

        MagickImage canvas;
        if (string.IsNullOrWhiteSpace(o.Color2))
        {
            canvas = new MagickImage(new MagickColor(o.Color1), cw, ch);
        }
        else
        {
            var settings = new MagickReadSettings { Width = cw, Height = ch };
            settings.SetDefine("gradient:direction", "SouthEast");
            canvas = new MagickImage($"gradient:{o.Color1}-{o.Color2}", settings);
        }

        using var p = (MagickImage)photo.Clone();
        var radius = Math.Min(pw, ph) * Math.Max(0, o.CornerPct) / 100.0;
        if (radius >= 1) RoundCorners(p, radius);

        var x = (int)((cw - pw) / 2);
        var y = (int)((ch - ph) / 2);
        if (o.Shadow)
        {
            var sigma = Math.Max(4, Math.Max(pw, ph) / 60.0);
            var offset = (int)Math.Round(sigma * 0.8);
            using var sh = new MagickImage(MagickColors.Transparent, cw, ch);
            new Drawables()
                .FillColor(new MagickColor(0, 0, 0, 110))
                .RoundRectangle(x, y + offset, x + pw - 1, y + offset + ph - 1, radius, radius)
                .Draw(sh);
            sh.Blur(0, sigma, Channels.All);
            canvas.Composite(sh, 0, 0, CompositeOperator.Over);
        }
        canvas.Composite(p, x, y, CompositeOperator.Over);
        return canvas;
    }

    public static string AddBackground(string input, BackgroundOptions o)
    {
        using var img = Load(input);
        using var result = RenderBackground(img, o);
        var outPath = OutputPaths.For(input, "png", "fundo");
        result.Write(outPath, MagickFormat.Png);
        return outPath;
    }

    private static void RoundCorners(MagickImage img, double radius)
    {
        using var mask = new MagickImage(MagickColors.Transparent, img.Width, img.Height);
        new Drawables()
            .FillColor(MagickColors.White)
            .RoundRectangle(0, 0, img.Width - 1, img.Height - 1, radius, radius)
            .Draw(mask);
        img.Alpha(AlphaOption.Set);
        img.Composite(mask, CompositeOperator.DstIn);
    }

    public static MagickImage RenderCollage(IReadOnlyList<string> files, CollageOptions o, int scaleDown = 1)
    {
        var cell = (uint)Math.Max(16, o.Cell / Math.Max(1, scaleDown));
        var gap = Math.Max(0, o.Spacing / Math.Max(1, scaleDown));
        var corner = Math.Max(0, o.Corner / (double)Math.Max(1, scaleDown));
        var tiles = new List<MagickImage>();
        try
        {
            foreach (var f in files)
            {
                var t = Load(f);
                switch (o.Layout)
                {
                    case "row":
                        t.Resize(new MagickGeometry(0, cell));
                        break;
                    case "column":
                        t.Resize(new MagickGeometry(cell, 0));
                        break;
                    default:
                        t.Resize(new MagickGeometry(cell, cell) { FillArea = true });
                        var cx = (int)(t.Width - cell) / 2;
                        var cy = (int)(t.Height - cell) / 2;
                        t.Crop(new MagickGeometry(cx, cy, cell, cell));
                        t.ResetPage();
                        break;
                }
                if (corner >= 1) RoundCorners(t, corner);
                tiles.Add(t);
            }

            int W, H;
            var positions = new List<(int X, int Y)>();
            switch (o.Layout)
            {
                case "row":
                {
                    var x = gap;
                    foreach (var t in tiles)
                    {
                        positions.Add((x, gap));
                        x += (int)t.Width + gap;
                    }
                    W = x;
                    H = (int)cell + 2 * gap;
                    break;
                }
                case "column":
                {
                    var y = gap;
                    foreach (var t in tiles)
                    {
                        positions.Add((gap, y));
                        y += (int)t.Height + gap;
                    }
                    W = (int)cell + 2 * gap;
                    H = y;
                    break;
                }
                default:
                {
                    var cols = (int)Math.Ceiling(Math.Sqrt(tiles.Count));
                    var rows = (int)Math.Ceiling(tiles.Count / (double)cols);
                    for (var i = 0; i < tiles.Count; i++)
                    {
                        positions.Add((gap + (i % cols) * ((int)cell + gap), gap + (i / cols) * ((int)cell + gap)));
                    }
                    W = gap + cols * ((int)cell + gap);
                    H = gap + rows * ((int)cell + gap);
                    break;
                }
            }

            var canvas = new MagickImage(new MagickColor(o.Background), (uint)Math.Max(1, W), (uint)Math.Max(1, H));
            for (var i = 0; i < tiles.Count; i++) canvas.Composite(tiles[i], positions[i].X, positions[i].Y, CompositeOperator.Over);
            return canvas;
        }
        finally
        {
            foreach (var t in tiles) t.Dispose();
        }
    }

    public static string Collage(IReadOnlyList<string> files, CollageOptions o)
    {
        using var img = RenderCollage(files, o);
        var outPath = OutputPaths.Unique(OutputPaths.DirFor(files[0]), T("Colagem"), "png");
        img.Write(outPath, MagickFormat.Png);
        return outPath;
    }

    public static void ApplyRedact(MagickImage img, IReadOnlyList<PixelRect> rects, RedactMode mode)
    {
        foreach (var raw in rects)
        {
            var r = Clamp(raw, (int)img.Width, (int)img.Height);
            using var region = (MagickImage)img.CloneArea(new MagickGeometry(r.X, r.Y, (uint)r.Width, (uint)r.Height));
            switch (mode)
            {
                case RedactMode.Blur:
                    region.GaussianBlur(0, Math.Clamp(Math.Max(r.Width, r.Height) / 10.0, 6, 60));
                    region.GaussianBlur(0, Math.Clamp(Math.Max(r.Width, r.Height) / 10.0, 6, 60));
                    break;
                case RedactMode.Pixelate:
                    var block = Math.Max(6, Math.Max(r.Width, r.Height) / 14);
                    region.Sample((uint)Math.Max(1, r.Width / block), (uint)Math.Max(1, r.Height / block));
                    region.Sample(new MagickGeometry((uint)r.Width, (uint)r.Height) { IgnoreAspectRatio = true });
                    break;
                case RedactMode.Fill:
                    region.Colorize(MagickColors.Black, new Percentage(100));
                    region.Alpha(AlphaOption.Opaque);
                    break;
            }
            img.Composite(region, r.X, r.Y, CompositeOperator.Over);
        }
    }

    public static string Redact(string input, IReadOnlyList<PixelRect> rects, RedactMode mode)
    {
        using var img = Load(input);
        ApplyRedact(img, rects, mode);
        return Save(img, input, "oculto", prefersAlpha: false);
    }

    private static string Save(MagickImage img, string input, string suffix, bool prefersAlpha)
    {
        var ext = SameOrFallback(input, prefersAlpha || img.HasAlpha);
        if (ext == "jpg") FlattenOnWhite(img);
        var outPath = OutputPaths.For(input, ext, suffix);
        img.Write(outPath, FormatOf(ext));
        return outPath;
    }
}
