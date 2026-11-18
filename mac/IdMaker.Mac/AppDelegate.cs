using IdMaker.Core;

namespace IdMaker.Mac;

[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private NSWindow? _window;

    public override void DidFinishLaunching(NSNotification notification)
    {
        // shortcut: a placeholder window until the form is built (GATES.md, M4); it says so rather than look finished.
        var window = new NSWindow(
            new CGRect(0, 0, 720, 420),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable,
            NSBackingStore.Buffered,
            false)
        {
            Title = "Alpha Premier ID Maker",
        };
        var label = NSTextField.CreateLabel($"Alpha Premier ID Maker {CardConfig.AppVersion} for Mac is being built.\nUse the Windows app to make cards for now.");
        label.Alignment = NSTextAlignment.Center;
        label.Frame = new CGRect(40, 170, 640, 80);
        window.ContentView!.AddSubview(label);
        window.Center();
        window.MakeKeyAndOrderFront(this);
        _window = window;
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;
}
