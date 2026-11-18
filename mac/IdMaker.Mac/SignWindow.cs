// Signing, on the whole screen so the pen has room.
//
// The pad records strokes in its own units (1000 across), with the pen's
// pressure when a tablet reports one, exactly as the Windows pad does, so a
// signature made on either prints the same.

using CoreGraphics;
using IdMaker.Core;

namespace IdMaker.Mac;

internal sealed class SignaturePadView : NSView
{
    public const double Units = 1000;                   // the pad's width in its own units
    private const double MinInkUnits = 2.5;             // line width at the lightest touch
    private const double MaxInkUnits = 7.8;
    private const double JitterPoints = 1.5;            // movements smaller than this are the hand, not the signature

    private readonly List<List<InkPoint>> _strokes = new();
    private List<InkPoint>? _current;

    public bool SawPressure { get; private set; }
    public string Hint { get; set; } = "Sign here";

    public override bool IsFlipped => true;
    public override bool AcceptsFirstResponder() => true;

    private double Scale => Bounds.Width / Units;

    public DrawnSignature? Value
    {
        get => _strokes.Count == 0 ? null : new DrawnSignature(_strokes.Select(s => (IReadOnlyList<InkPoint>)s.ToList()).ToList());
        set
        {
            _strokes.Clear();
            if (value is not null) foreach (var stroke in value.Strokes) _strokes.Add(stroke.ToList());
            NeedsDisplay = true;
        }
    }

    public void Clear()
    {
        _strokes.Clear();
        _current = null;
        NeedsDisplay = true;
    }

    // A tablet's events carry how hard the pen presses; a mouse's do not.
    private static double PressureOf(NSEvent e) =>
        e.Subtype == NSEventSubtype.TabletPoint ? Math.Clamp(e.Pressure, 0f, 1f) : -1;

    private InkPoint ToPad(NSEvent e)
    {
        var p = ConvertPointFromView(e.LocationInWindow, null);
        return new InkPoint(Math.Clamp(p.X, 0, Bounds.Width) / Scale, Math.Clamp(p.Y, 0, Bounds.Height) / Scale, PressureOf(e));
    }

    public override void MouseDown(NSEvent theEvent)
    {
        var first = ToPad(theEvent);
        _current = new List<InkPoint> { first };
        if (first.HasPressure) SawPressure = true;
        NeedsDisplay = true;
    }

    public override void MouseDragged(NSEvent theEvent)
    {
        if (_current is null) return;
        var next = ToPad(theEvent);
        if (next.HasPressure) SawPressure = true;
        var last = _current[^1];
        var moved = Math.Sqrt((next.X - last.X) * (next.X - last.X) + (next.Y - last.Y) * (next.Y - last.Y)) * Scale;
        if (moved < JitterPoints)
        {
            // standing still but pressing harder still thickens the line
            if (next.Pressure > last.Pressure) _current[^1] = last with { Pressure = next.Pressure };
            return;
        }
        _current.Add(next);
        NeedsDisplay = true;
    }

    public override void MouseUp(NSEvent theEvent)
    {
        if (_current is null) return;
        _strokes.Add(_current);
        _current = null;
        NeedsDisplay = true;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        var context = NSGraphicsContext.CurrentContext?.CGContext;
        if (context is null) return;
        context.SetFillColor(new CGColor(1, 1, 1, 1));
        context.FillRect(Bounds);
        // the line to sign on
        var y = Bounds.Height * 0.72;
        context.SetStrokeColor(new CGColor(0.78f, 0.78f, 0.78f, 1));
        context.SetLineWidth((nfloat)Math.Max(1, Bounds.Height / 180));
        context.MoveTo((nfloat)(Bounds.Width * 0.08), (nfloat)y);
        context.AddLineToPoint((nfloat)(Bounds.Width * 0.92), (nfloat)y);
        context.StrokePath();

        var all = _current is null ? _strokes : _strokes.Append(_current).ToList();
        if (!all.Any(s => s.Count > 0)) return;
        var drawn = new DrawnSignature(all.Select(s => (IReadOnlyList<InkPoint>)s).ToList());
        MacCardRenderer.DrawInk(context, SignatureInk.Segments(drawn, MinInkUnits, MaxInkUnits), new CGColor(0.11f, 0.11f, 0.10f, 1), 0, 0, Scale, Scale);
    }
}

/// <summary>The signing screen. `Ask` shows it and gives back the signature, or null when it was cancelled.</summary>
internal static class SignWindow
{
    public static DrawnSignature? Ask(string firstName, DrawnSignature? existing)
    {
        var screen = NSScreen.MainScreen?.VisibleFrame ?? new CGRect(0, 0, 1280, 800);
        var window = new NSWindow(screen, NSWindowStyle.Titled, NSBackingStore.Buffered, false)
        {
            Title = firstName.Length > 0 ? $"{firstName}, please sign" : "Please sign",
            ReleasedWhenClosed = false,
        };
        var pad = new SignaturePadView { TranslatesAutoresizingMaskIntoConstraints = false, Value = existing };
        DrawnSignature? result = null;
        var status = Ui.Note("Sign with the pen or the mouse. A pen tablet draws thicker where you press harder.");
        var done = Ui.Button("Use this signature", () =>
        {
            result = pad.Value;
            NSApplication.SharedApplication.StopModal();
        }, primary: true);
        var clear = Ui.Button("Start again", pad.Clear);
        var cancel = Ui.Button("Cancel", () => NSApplication.SharedApplication.StopModal());
        cancel.KeyEquivalent = "\u001b";
        var buttons = Ui.Row(12, done, clear, cancel, status);
        var content = window.ContentView!;
        content.AddSubview(pad);
        content.AddSubview(buttons);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            pad.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 24),
            pad.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -24),
            pad.TopAnchor.ConstraintEqualTo(content.TopAnchor, 24),
            pad.BottomAnchor.ConstraintEqualTo(buttons.TopAnchor, -16),
            buttons.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 24),
            buttons.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -20),
        });
        window.MakeKeyAndOrderFront(null);
        NSApplication.SharedApplication.RunModalForWindow(window);
        window.OrderOut(null);
        return result;
    }
}
