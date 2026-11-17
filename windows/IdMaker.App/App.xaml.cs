using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace IdMaker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // One place for anything that was not expected: say so, write it
        // down where it can be sent to whoever maintains the app, and keep
        // the window open — a card half made is worth more than a clean exit.
        DispatcherUnhandledException += OnUnhandled;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = Path.Combine(Core.FileStore.DefaultDirectory, "last-error.txt");
        try
        {
            Directory.CreateDirectory(Core.FileStore.DefaultDirectory);
            File.WriteAllText(log, $"{DateTime.Now:O}\r\n{Core.CardConfig.AppName} {Core.CardConfig.AppVersion}\r\n{e.Exception}");
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException) { /* the message below still says what happened */ }
        MessageBox.Show(
            $"Something went wrong, and the last thing you did may not have finished.\n\n{e.Exception.Message}\n\nThe details were saved to:\n{log}",
            Core.CardConfig.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}

/// <summary>The brushes and styles in Theme.xaml, by name, for the parts of the window built in code.</summary>
public static class Theme
{
    // SAFETY (both casts): every key asked for is defined in Theme.xaml with this type, and the
    // window-construction test opens every screen, so a wrong key fails there rather than in use.
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);
}
