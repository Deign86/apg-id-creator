// Drawing the card on a Mac, with Core Graphics.
//
// The Mac counterpart of the Windows app's CardRenderer, layer for layer:
// artwork, photo cut to its window, the band and gold line back on top,
// name, title and number as outlines, signature, QR. The card is drawn into
// plain pixels at any pixels-per-millimetre, so the preview and the PDF are
// the same drawing, and it is held to the same reference card as Windows'
// (`test/golden-front.png`).

using System.Runtime.CompilerServices;
using CoreGraphics;
using IdMaker.Core;
using ImageIO;

namespace IdMaker.Mac;

/// <summary>The two sides of the card artwork, decoded.</summary>
public sealed record CardArt(CGImage Front, CGImage Back);

/// <summary>Pictures in and out of plain RGBA, with the Mac's own codecs.</summary>
public static class MacPictures
{
    internal static nfloat F(double value) => (nfloat)value;

    internal static readonly CGColorSpace Srgb = CGColorSpace.CreateSrgb() ?? throw new InvalidOperationException("This Mac has no sRGB colour space.");

    /// <summary>Open a picture file as the Mac decodes it. Null when it is not a picture the Mac can read.</summary>
    public static CGImage? Load(string path)
    {
        using var source = CGImageSource.FromUrl(NSUrl.FromFilename(path));
        return source is null || source.ImageCount < 1 ? null : source.CreateImage(0, new CGImageOptions());
    }

    /// <summary>Straight RGBA as a picture the Mac can draw.</summary>
    public static CGImage ToImage(RgbaImage image)
    {
        var data = new byte[image.Data.Length];
        for (var i = 0; i < data.Length; i += 4)
        {
            int a = image.Data[i + 3];
            data[i] = (byte)(image.Data[i] * a / 255);
            data[i + 1] = (byte)(image.Data[i + 1] * a / 255);
            data[i + 2] = (byte)(image.Data[i + 2] * a / 255);
            data[i + 3] = (byte)a;
        }
        using var provider = new CGDataProvider(data);
        return new CGImage(image.Width, image.Height, 8, 32, image.Width * 4, Srgb, CGBitmapFlags.PremultipliedLast, provider, null, true, CGColorRenderingIntent.Default);
    }

    /// <summary>
    /// A drawing surface of this many pixels whose y grows downwards, as the
    /// card's millimetres do, and the bytes it draws into (premultiplied RGBA).
    /// </summary>
    internal static (CGBitmapContext Context, byte[] Pixels) Surface(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var context = new CGBitmapContext(pixels, width, height, 8, width * 4, Srgb, CGImageAlphaInfo.PremultipliedLast);
        context.InterpolationQuality = CGInterpolationQuality.High;
        context.TranslateCTM(0, height);
        context.ScaleCTM(1, -1);
        return (context, pixels);
    }

    /// <summary>A picture, the right way up, in a surface whose y grows downwards.</summary>
    internal static void Draw(CGContext context, CGImage image, CGRect where)
    {
        context.SaveState();
        context.TranslateCTM(where.X, where.Y + where.Height);
        context.ScaleCTM(1, -1);
        context.DrawImage(new CGRect(0, 0, where.Width, where.Height), image);
        context.RestoreState();
    }

    /// <summary>Premultiplied pixels back to straight RGBA.</summary>
    internal static RgbaImage ToRgba(byte[] premultiplied, int width, int height)
    {
        var data = new byte[premultiplied.Length];
        for (var i = 0; i < data.Length; i += 4)
        {
            int a = premultiplied[i + 3];
            if (a == 0) continue;
            data[i] = (byte)Math.Min(255, premultiplied[i] * 255 / a);
            data[i + 1] = (byte)Math.Min(255, premultiplied[i + 1] * 255 / a);
            data[i + 2] = (byte)Math.Min(255, premultiplied[i + 2] * 255 / a);
            data[i + 3] = (byte)a;
        }
        return new RgbaImage(width, height, data);
    }

    /// <summary>Any decoded picture as straight RGBA.</summary>
    public static RgbaImage ToRgba(CGImage image)
    {
        int width = (int)image.Width, height = (int)image.Height;
        var (context, pixels) = Surface(width, height);
        using (context) Draw(context, image, new CGRect(0, 0, width, height));
        return ToRgba(pixels, width, height);
    }

    /// <summary>Open a picture file as straight RGBA, or say why not.</summary>
    public static (RgbaImage? Image, string Error) Decode(string path)
    {
        // shortcut: a phone's "which way up" note is not honoured yet, so a portrait JPEG may open on its side; revisit with the photo step (GATES.md, M4).
        using var image = Load(path);
        return image is null
            ? (null, "This file couldn’t be opened as a picture. Try saving it again as PNG or JPG.")
            : (ToRgba(image), "");
    }

    public static void SavePng(RgbaImage image, string path)
    {
        using var picture = ToImage(image);
        using var destination = CGImageDestination.Create(NSUrl.FromFilename(path), "public.png", 1)
            ?? throw new IOException($"The picture could not be saved to {path}.");
        destination.AddImage(picture);
        if (!destination.Close()) throw new IOException($"The picture could not be saved to {path}.");
    }
}

public sealed class MacCardRenderer
{
    private readonly MacTextMeasurer _measurer;
    // a photo is converted once and kept for as long as its pixels are
    private readonly ConditionalWeakTable<RgbaImage, CGImage> _photos = new();
    private QrCode? _qr;
    private string _qrKey = "";

    public MacCardRenderer(MacTextMeasurer measurer) { _measurer = measurer; }

    public ITextMeasurer Measurer => _measurer;

    private static nfloat F(double value) => MacPictures.F(value);

    private static CGColor Solid(string hex)
    {
        // "#RRGGBB", as every colour in CardConfig is written.
        double Part(int at) => Convert.ToInt32(hex.Substring(at, 2), 16) / 255.0;
        return new CGColor(F(Part(1)), F(Part(3)), F(Part(5)), 1);
    }

    private static CGPath Polygons(IEnumerable<IReadOnlyList<PointMm>> polygons, double pxPerMm)
    {
        var path = new CGPath();
        foreach (var polygon in polygons)
        {
            if (polygon.Count < 3) continue;
            path.MoveToPoint(F(polygon[0].X * pxPerMm), F(polygon[0].Y * pxPerMm));
            for (var i = 1; i < polygon.Count; i++) path.AddLineToPoint(F(polygon[i].X * pxPerMm), F(polygon[i].Y * pxPerMm));
            path.CloseSubpath();
        }
        return path;
    }

    private static RgbaImage Render(int width, int height, Action<CGContext> draw)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var (context, pixels) = MacPictures.Surface(width, height);
        using (context) draw(context);
        return MacPictures.ToRgba(pixels, width, height);
    }

    /* ---- the two sides -------------------------------------------- */

    public RgbaImage RenderFront(CardArt art, CardGeometry geo, CardData? data, double pxPerMm)
    {
        var (w, h) = CardConfig.CardSizePx(pxPerMm);
        return Render(w, h, context => DrawFront(context, art, geo, data, pxPerMm, w, h));
    }

    public RgbaImage RenderBack(CardArt art, double pxPerMm, bool rotate180)
    {
        var (w, h) = CardConfig.CardSizePx(pxPerMm);
        return Render(w, h, context =>
        {
            if (rotate180)
            {
                context.TranslateCTM(w, h);
                context.RotateCTM(F(Math.PI));
            }
            MacPictures.Draw(context, art.Back, new CGRect(0, 0, w, h));
        });
    }

    private void DrawFront(CGContext context, CardArt art, CardGeometry geo, CardData? data, double ppm, int w, int h)
    {
        var full = new CGRect(0, 0, w, h);
        MacPictures.Draw(context, art.Front, full);

        if (data is { Photo: not null, Placement: not null })
        {
            var p = data.Placement;
            context.SaveState();
            context.AddPath(Polygons(new[] { geo.PhotoClip }, ppm));
            context.Clip();
            var photo = _photos.GetValue(data.Photo, MacPictures.ToImage);
            var where = new CGRect(p.X * ppm, p.Y * ppm, p.W * ppm, p.H * ppm);
            if (p.ShortBy > 0)
            {
                // A photo too closely cropped to reach the bottom of its
                // window fades out over its last few millimetres, as on
                // Windows: drawn on its own layer, then kept only where a
                // white-to-clear gradient is.
                var fade = Math.Min(CardConfig.Photo.FadeMm, p.H / 3);
                var bottom = (p.Y + p.H) * ppm;
                context.BeginTransparencyLayer();
                MacPictures.Draw(context, photo, where);
                context.SetBlendMode(CGBlendMode.DestinationIn);
                using var gradient = new CGGradient(MacPictures.Srgb, new[] { new CGColor(1, 1, 1, 1), new CGColor(1, 1, 1, 0) }, new nfloat[] { 0, 1 });
                context.DrawLinearGradient(gradient, new CGPoint(0, bottom - fade * ppm), new CGPoint(0, bottom),
                    CGGradientDrawingOptions.DrawsBeforeStartLocation | CGGradientDrawingOptions.DrawsAfterEndLocation);
                context.EndTransparencyLayer();
            }
            else
            {
                MacPictures.Draw(context, photo, where);
            }
            context.RestoreState();
        }

        // the band and the gold line, back on top of the photo
        if (geo.FrontClip.Count > 0)
        {
            context.SaveState();
            context.AddPath(Polygons(geo.FrontClip, ppm));
            context.Clip();
            MacPictures.Draw(context, art.Front, full);
            context.RestoreState();
        }

        if (data is null) return;

        var layout = TextFit.Layout(_measurer, geo, data.Name, data.Title, data.Year, data.Seq);
        DrawText(context, geo.Name, layout.Name, ppm);
        DrawText(context, geo.Title, layout.Title, ppm);
        DrawText(context, geo.EmployeeNo, layout.EmployeeNo, ppm);

        if (data.Signature is { HasInk: true }) DrawSignature(context, data.Signature, geo.Signature, ppm);
        DrawQr(context, geo.Qr, ppm);
    }

    private void DrawText(CGContext context, TextSpec spec, FittedText fit, double ppm)
    {
        if (fit.Lines.Count == 0) return;
        var place = TextFit.Place(_measurer, spec, fit, ppm);
        context.SetFillColor(Solid(spec.Color));
        for (var i = 0; i < fit.Lines.Count; i++)
        {
            var width = _measurer.Width(spec.Font, place.Px, fit.Lines[i]);
            var x = spec.Align switch
            {
                HAlign.Center => place.AnchorX - width / 2,
                HAlign.Right => place.AnchorX - width,
                _ => place.AnchorX,
            };
            foreach (var (word, offset) in _measurer.Words(spec.Font, place.Px, fit.Lines[i]))
            {
                // Filled as outlines, not drawn as text: outlines land exactly
                // where they are put, the same on every computer.
                using var outline = _measurer.Outline(spec.Font, place.Px, word, x + offset, place.Baselines[i]);
                context.AddPath(outline);
                context.FillPath();
            }
        }
    }

    /* ---- signature ------------------------------------------------ */

    /// <summary>Draw strokes: x' = ox + x * scale. Shared by the card and the pad on screen.</summary>
    public static void DrawInk(CGContext context, IReadOnlyList<InkSegment> segments, CGColor color, double ox, double oy, double scale, double widthScale)
    {
        context.SetFillColor(color);
        context.SetStrokeColor(color);
        context.SetLineCap(CGLineCap.Round);
        context.SetLineJoin(CGLineJoin.Round);
        foreach (var s in segments)
        {
            if (s.Dot)
            {
                var r = s.Width * widthScale / 2;
                context.FillEllipseInRect(new CGRect(ox + s.X1 * scale - r, oy + s.Y1 * scale - r, r * 2, r * 2));
                continue;
            }
            context.SetLineWidth(F(s.Width * widthScale));
            context.MoveTo(F(ox + s.X1 * scale), F(oy + s.Y1 * scale));
            context.AddCurveToPoint(
                F(ox + s.C1X * scale), F(oy + s.C1Y * scale),
                F(ox + s.C2X * scale), F(oy + s.C2Y * scale),
                F(ox + s.X2 * scale), F(oy + s.Y2 * scale));
            context.StrokePath();
        }
    }

    /// <summary>A scan's ink as a picture in one colour.</summary>
    public static CGImage TintedScan(ScannedSignature scan, string hex)
    {
        int Part(int at) => Convert.ToInt32(hex.Substring(at, 2), 16);
        var data = new byte[scan.Width * scan.Height * 4];
        for (var i = 0; i < scan.Alpha.Length; i++)
        {
            data[i * 4] = (byte)Part(1);
            data[i * 4 + 1] = (byte)Part(3);
            data[i * 4 + 2] = (byte)Part(5);
            data[i * 4 + 3] = scan.Alpha[i];
        }
        return MacPictures.ToImage(new RgbaImage(scan.Width, scan.Height, data));
    }

    private static void DrawSignature(CGContext context, Signature signature, SignatureSpec spec, double ppm)
    {
        if (signature is ScannedSignature scan)
        {
            var fit = SignatureInk.FitInto(spec, 0, 0, scan.Width, scan.Height);
            using var white = TintedScan(scan, spec.Color);
            MacPictures.Draw(context, white, new CGRect(fit.Left * ppm, fit.Top * ppm, scan.Width * fit.Scale * ppm, scan.Height * fit.Scale * ppm));
            return;
        }
        if (signature is not DrawnSignature drawn) return;
        var (minX, minY, inkW, inkH) = SignatureInk.Bounds(drawn);
        var f = SignatureInk.FitInto(spec, minX, minY, inkW, inkH);
        // Widths are asked for in pad units that come out as StrokeMm on the card.
        var segments = SignatureInk.Segments(drawn, spec.MinStrokeMm / f.Scale, spec.StrokeMm / f.Scale);
        var scale = f.Scale * ppm;
        DrawInk(context, segments, Solid(spec.Color), (f.Left - minX * f.Scale) * ppm, (f.Top - minY * f.Scale) * ppm, scale, scale);
    }

    /* ---- QR -------------------------------------------------------- */

    private void DrawQr(CGContext context, QrSpec spec, double ppm)
    {
        var key = spec.Content + "|" + spec.ErrorCorrection;
        if (_qr is null || _qrKey != key) { _qr = QrCode.Encode(spec.Content, spec.ErrorCorrection); _qrKey = key; }
        var n = _qr.Size;

        // Everything is snapped to whole PRINT pixels, so the code stays
        // crisp at 300 DPI whatever resolution this is being drawn at.
        var printPpm = CardConfig.PrintPxPerMm;
        double Snap(double mm) => Math.Floor(mm * printPpm + 0.5) / printPpm;
        double x0 = Snap(spec.X), y0 = Snap(spec.Y), side = Snap(spec.Size);
        context.SetShouldAntialias(false);
        context.SetFillColor(Solid(spec.Background));
        context.FillRect(new CGRect(x0 * ppm, y0 * ppm, side * ppm, side * ppm));

        // module size: whole print pixels, leaving a two-module quiet zone each side
        var moduleMm = Math.Max(1, Math.Floor(side * printPpm / (n + 4))) / printPpm;
        var codeMm = n * moduleMm;
        double left = Snap(x0 + (side - codeMm) / 2), top = Snap(y0 + (side - codeMm) / 2);

        // one path for the whole code: neighbouring modules share an edge exactly, so no hairlines between them
        var code = new CGPath();
        for (var r = 0; r < n; r++)
        {
            for (var c = 0; c < n; c++)
            {
                if (!_qr.IsDark(r, c)) continue;
                var end = c;
                while (end + 1 < n && _qr.IsDark(r, end + 1)) end++;
                code.AddRect(new CGRect((left + c * moduleMm) * ppm, (top + r * moduleMm) * ppm, (end + 1 - c) * moduleMm * ppm, moduleMm * ppm));
                c = end;
            }
        }
        context.SetFillColor(Solid(spec.Color));
        context.AddPath(code);
        context.FillPath();
        context.SetShouldAntialias(true);
    }
}
