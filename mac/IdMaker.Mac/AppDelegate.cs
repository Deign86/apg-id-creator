namespace IdMaker.Mac;

[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private MainWindow? _main;

    public override void DidFinishLaunching(NSNotification notification)
    {
        _main = new MainWindow();
        _main.Window.MakeKeyAndOrderFront(this);
        if (OperatingSystem.IsMacOSVersionAtLeast(14)) NSApplication.SharedApplication.Activate();
        else NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;
}
