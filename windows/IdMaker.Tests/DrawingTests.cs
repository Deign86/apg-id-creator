// The front of the card as a list of things to draw (CardDrawing.Front):
// what is on it and in what order. That the drawn card matches the one the
// web app printed is held by the renderers' own tests against
// test/golden-front.png; these hold the recipe, without a renderer.

using IdMaker.Core;

namespace IdMaker.Tests;

public class DrawingTests
{
    private static readonly FakeMeasurer Measurer = new();
    private const double Ppm = 300 / 25.4;

    private static CardData Card(Signature? signature = null, PhotoPlacement? placement = null) => new(
        "Ana S. Reyes", "HR Associate", 2026, 124, new RgbaImage(4, 4), placement ?? new PhotoPlacement(20, 12, 30, 50, 0), signature);

    private static string Kinds(IEnumerable<DrawOp> ops) => string.Join(" ", ops.Select(op => op.GetType().Name.Replace("Op", "")));

    [Fact]
    public void An_empty_card_is_the_artwork_and_its_band()
    {
        var ops = CardDrawing.Front(Measurer, CardGeometry.BuiltIn, null, Ppm);
        Assert.Equal("Artwork Artwork", Kinds(ops));
        Assert.Null(Assert.IsType<ArtworkOp>(ops[0]).Clip);
        Assert.Same(CardGeometry.BuiltIn.FrontClip, Assert.IsType<ArtworkOp>(ops[1]).Clip);
    }

    [Fact]
    public void A_card_is_drawn_artwork_photo_band_text_signature_code_in_that_order()
    {
        var ops = CardDrawing.Front(Measurer, CardGeometry.BuiltIn, Card(Session.ExampleSignature()), Ppm);
        var kinds = Kinds(ops).Split(' ');
        Assert.Equal(new[] { "Artwork", "Photo", "Artwork" }, kinds.Take(3));
        Assert.Equal(new[] { "Ink", "Qr" }, kinds.TakeLast(2));
        Assert.All(kinds.Skip(3).SkipLast(2), kind => Assert.Equal("Word", kind));

        // the words, in reading order: the name, the title in capitals, the number
        var words = ops.OfType<WordOp>().ToList();
        Assert.Equal("Ana S. Reyes HR ASSOCIATE EMPLOYEE NO: APG - 2026 - 124", string.Join(" ", words.Select(w => w.Word)));
        Assert.Equal(CardGeometry.BuiltIn.Name.Color, words[0].Color);
        Assert.Equal(CardFont.Name, words[0].Font);
        Assert.Equal(CardFont.Number, words[^1].Font);
    }

    [Fact]
    public void Each_word_starts_where_the_line_s_fit_and_the_spaces_before_it_put_it()
    {
        var geo = CardGeometry.BuiltIn;
        var data = Card();
        var words = CardDrawing.Front(Measurer, geo, data, Ppm).OfType<WordOp>().Where(w => w.Font == CardFont.Number).ToList();
        var fit = TextFit.Layout(Measurer, geo, data.Name, data.Title, data.Year, data.Seq).EmployeeNo;
        var place = TextFit.Place(Measurer, geo.EmployeeNo, fit, Ppm);
        var line = Assert.Single(fit.Lines);

        // right-aligned: the line ends at the anchor
        var width = Measurer.Width(CardFont.Number, place.Px, line);
        Assert.Equal(place.AnchorX - width, words[0].X, 9);
        var last = words[^1];
        Assert.Equal(place.AnchorX, last.X + Measurer.Width(CardFont.Number, place.Px, last.Word), 6);
        Assert.All(words, w => Assert.Equal(place.Baselines[0], w.Baseline, 9));
        Assert.All(words, w => Assert.Equal(place.Px, w.Px, 9));

        // a word's offset is the widths of the words and single spaces before it
        var offsets = CardDrawing.Words(Measurer, CardFont.Number, 10, "AB  C").ToList();
        Assert.Equal(new[] { "AB", "C" }, offsets.Select(o => o.Word));
        Assert.Equal(0, offsets[0].X, 9);
        Assert.Equal(Measurer.Width(CardFont.Number, 10, "AB  "), offsets[1].X, 9);
    }

    [Fact]
    public void A_photo_that_stops_short_of_its_window_fades_over_its_last_millimetres()
    {
        var whole = Assert.Single(CardDrawing.Front(Measurer, CardGeometry.BuiltIn, Card(), Ppm).OfType<PhotoOp>());
        Assert.False(whole.Fades);
        Assert.Equal(20 * Ppm, whole.X, 9);
        Assert.Equal(12 * Ppm, whole.Y, 9);
        Assert.Equal(30 * Ppm, whole.W, 9);
        Assert.Equal(50 * Ppm, whole.H, 9);
        Assert.Same(CardGeometry.BuiltIn.PhotoClip, whole.Clip);

        var shortBy = Assert.Single(CardDrawing.Front(Measurer, CardGeometry.BuiltIn, Card(placement: new PhotoPlacement(20, 12, 30, 50, 2)), Ppm).OfType<PhotoOp>());
        Assert.True(shortBy.Fades);
        Assert.Equal(62 * Ppm, shortBy.FadeTo, 9);
        Assert.Equal((62 - Math.Min(CardConfig.Photo.FadeMm, 50.0 / 3)) * Ppm, shortBy.FadeFrom, 6);

        // no photo, no photo operation
        var none = CardDrawing.Front(Measurer, CardGeometry.BuiltIn, Card() with { Photo = null }, Ppm);
        Assert.Empty(none.OfType<PhotoOp>());
    }

    [Fact]
    public void A_signature_is_fitted_into_its_box_drawn_or_scanned()
    {
        var spec = CardGeometry.BuiltIn.Signature;
        var drawn = Session.ExampleSignature();
        var ink = Assert.Single(CardDrawing.Front(Measurer, CardGeometry.BuiltIn, Card(drawn), Ppm).OfType<InkOp>());
        var (minX, minY, inkW, inkH) = SignatureInk.Bounds(drawn);
        var fit = SignatureInk.FitInto(spec, minX, minY, inkW, inkH);
        Assert.Equal(fit.Scale * Ppm, ink.Scale, 9);
        Assert.Equal(ink.Scale, ink.WidthScale, 9);
        Assert.Equal((fit.Left - minX * fit.Scale) * Ppm, ink.Ox, 9);
        Assert.Equal(spec.Color, ink.Color);
        Assert.NotEmpty(ink.Segments);

        var scan = new ScannedSignature(200, 50, new byte[200 * 50], "sign.png");
        var picture = Assert.Single(CardDrawing.Front(Measurer, CardGeometry.BuiltIn, Card(scan), Ppm).OfType<ScanOp>());
        var scanFit = SignatureInk.FitInto(spec, 0, 0, 200, 50);
        Assert.Equal(scanFit.Left * Ppm, picture.X, 9);
        Assert.Equal(200 * scanFit.Scale * Ppm, picture.W, 9);
        Assert.InRange(picture.X + picture.W, spec.Box.X * Ppm, (spec.Box.X + spec.Box.W) * Ppm + 1e-6);

        // no signature, nothing drawn for one
        var unsigned = CardDrawing.Front(Measurer, CardGeometry.BuiltIn, Card(), Ppm);
        Assert.Empty(unsigned.OfType<InkOp>());
        Assert.Empty(unsigned.OfType<ScanOp>());
    }

    [Fact]
    public void The_code_is_whole_print_pixels_inside_its_square_and_every_dark_module_is_in_one_run()
    {
        var spec = CardGeometry.BuiltIn.Qr;
        var qr = Assert.Single(CardDrawing.Front(Measurer, CardGeometry.BuiltIn, Card(), Ppm).OfType<QrOp>());
        var code = QrCode.Encode(spec.Content, spec.ErrorCorrection);
        var print = CardConfig.PrintPxPerMm;

        // snapped to the print's pixels, with at least two modules of quiet on each side
        foreach (var mm in new[] { qr.X, qr.Y, qr.Side, qr.Left, qr.Top, qr.ModuleMm })
            Assert.Equal(Math.Round(mm * print), mm * print, 6);
        Assert.True(qr.Left - qr.X >= 2 * qr.ModuleMm - 1.0 / print);
        Assert.True(qr.Left + code.Size * qr.ModuleMm <= qr.X + qr.Side + 1e-9);
        Assert.Equal(spec.Background, qr.Background);
        Assert.Equal(spec.Color, qr.Color);

        // the runs cover exactly the dark modules
        var dark = new bool[code.Size, code.Size];
        foreach (var run in qr.Runs)
        {
            Assert.InRange(run.End, run.Start, code.Size - 1);
            for (var c = run.Start; c <= run.End; c++)
            {
                Assert.False(dark[run.Row, c]);
                dark[run.Row, c] = true;
            }
        }
        for (var r = 0; r < code.Size; r++)
            for (var c = 0; c < code.Size; c++)
                Assert.Equal(code.IsDark(r, c), dark[r, c]);
    }
}
