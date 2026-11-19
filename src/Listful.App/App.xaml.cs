using Microsoft.UI.Xaml;

namespace Listful.App;

/// <summary>Upstream <c>ListfulApp</c>: builds the library model and opens the one window.</summary>
public partial class App : Application
{
    private MainWindow? window;

    /// <summary>Windows-only: points the app at another preferences file, so a test run leaves the
    /// user's own settings (and lists folder) alone. Upstream passes <c>-libraryFolder</c> instead.</summary>
    public const string PreferencesVariable = "LISTFUL_PREFERENCES";

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Services.CrashLog.Append(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var path = Environment.GetEnvironmentVariable(PreferencesVariable) is { Length: > 0 } custom ? custom : Preferences.DefaultPath;
        window = new MainWindow(new Preferences(path));
        window.Activate();
    }
}
