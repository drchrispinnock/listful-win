using System.Globalization;
using Microsoft.UI.Dispatching;
using Velopack;
using Velopack.Sources;

namespace Listful.App.Services;

/// <summary>
/// Self-updating through Velopack (upstream's <c>Updater</c>: Sparkle), as in signet-windows.
/// Listful checks the GitHub releases of this repository once a day, offers any newer release it
/// finds, and installs it in place when the user restarts. Only the installed copy updates (the
/// Setup or Portable download); a copy run from a build folder reports that updates are unavailable,
/// as upstream hides Check for Updates under <c>swift run</c>.
/// <para>
/// Windows differences: Sparkle checks every update against an EdDSA key in the app; Velopack
/// relies on HTTPS from GitHub (and the code signature), so an update is only downloaded and
/// installed when the user says so. Upstream's "Automatically check for updates" is
/// <see cref="AutomaticallyChecks"/>.
/// </para>
/// </summary>
internal sealed class UpdaterService
{
    public const string RepositoryURL = "https://github.com/drchrispinnock/listful-win";
    public const string AutomaticChecksKey = "updatesAutomaticChecks";
    public const string LastCheckKey = "updatesLastCheck";
    /// <summary>Testing only: a local folder of <c>vpk pack</c> output to update from instead of GitHub.</summary>
    public const string SourceOverrideVariable = "LISTFUL_UPDATE_SOURCE";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    public abstract record UpdateState
    {
        public sealed record Idle : UpdateState;
        public sealed record Checking : UpdateState;
        public sealed record UpToDate : UpdateState;
        public sealed record Available(string Version) : UpdateState;
        public sealed record Downloading(string Version, int Percent) : UpdateState;
        /// <summary>Downloaded and staged: applied by Restart to Update, or at the next launch.</summary>
        public sealed record Ready(string Version) : UpdateState;
        public sealed record Failed(string Message) : UpdateState;
    }

    private readonly Preferences preferences;
    private readonly UpdateManager? manager;
    private UpdateInfo? pending;
    private DispatcherQueueTimer? timer;
    private UpdateState state = new UpdateState.Idle();

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;
    /// <summary>Raised when the daily check finds an update, so the window can offer it.</summary>
    public event Action? UpdateFound;

    public UpdateState State
    {
        get => state;
        private set
        {
            state = value;
            StateChanged?.Invoke();
        }
    }

    public UpdaterService(Preferences preferences)
    {
        this.preferences = preferences;
        try
        {
            var source = Environment.GetEnvironmentVariable(SourceOverrideVariable) is { Length: > 0 } folder
                ? (IUpdateSource)new SimpleFileSource(new DirectoryInfo(folder))
                : new GithubSource(RepositoryURL, null, false);
            manager = new UpdateManager(source);
        }
        catch (Exception)
        {
            manager = null;
        }
    }

    /// <summary>Whether this copy can update itself: it was installed by Velopack (Setup or Portable).</summary>
    public bool IsAvailable => manager?.IsInstalled == true;

    /// <summary>The running version, e.g. 0.6.0.</summary>
    public string CurrentVersion =>
        manager is { IsInstalled: true } m ? m.CurrentVersion?.ToString() ?? "?"
        : typeof(UpdaterService).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    /// <summary>The daily background check. On by default, as Sparkle's is.</summary>
    public bool AutomaticallyChecks
    {
        get => preferences.GetBool(AutomaticChecksKey, true);
        set => preferences.Set(AutomaticChecksKey, value);
    }

    public DateTimeOffset? LastCheckDate =>
        DateTimeOffset.TryParse(preferences.GetString(LastCheckKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;

    public bool CanCheck => IsAvailable && State is not (UpdateState.Checking or UpdateState.Downloading);

    /// <summary>Starts the daily schedule: a check soon after launch when one is due, then hourly looks at whether one is due.</summary>
    public void Start(DispatcherQueue queue)
    {
        if (!IsAvailable || timer is not null) return;
        timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromHours(1);
        timer.Tick += async (_, _) => await CheckIfDue();
        timer.Start();
        var first = queue.CreateTimer();
        first.Interval = TimeSpan.FromSeconds(10);
        first.IsRepeating = false;
        first.Tick += async (_, _) => await CheckIfDue();
        first.Start();
    }

    private async Task CheckIfDue()
    {
        if (!AutomaticallyChecks) return;
        if (LastCheckDate is { } last && DateTimeOffset.Now - last < CheckInterval) return;
        await Check();
        if (State is UpdateState.Available) UpdateFound?.Invoke();
    }

    /// <summary>Asks GitHub for a newer release, and offers it (<c>UpdateState.Available</c>).</summary>
    public async Task Check()
    {
        if (manager is null || !CanCheck || State is UpdateState.Ready) return;
        State = new UpdateState.Checking();
        try
        {
            pending = await manager.CheckForUpdatesAsync();
            preferences.Set(LastCheckKey, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            State = pending is null ? new UpdateState.UpToDate() : new UpdateState.Available(pending.TargetFullRelease.Version.ToString());
        }
        catch (Exception e)
        {
            State = new UpdateState.Failed($"Couldn't check for updates: {e.Message}");
        }
    }

    /// <summary>Downloads the update found by the last check and stages it.</summary>
    public async Task Download()
    {
        if (manager is null || pending is null) return;
        var version = pending.TargetFullRelease.Version.ToString();
        var queue = DispatcherQueue.GetForCurrentThread();
        State = new UpdateState.Downloading(version, 0);
        try
        {
            await manager.DownloadUpdatesAsync(pending, percent =>
                queue?.TryEnqueue(() => { if (State is UpdateState.Downloading) State = new UpdateState.Downloading(version, percent); }));
            State = new UpdateState.Ready(version);
        }
        catch (Exception e)
        {
            State = new UpdateState.Failed($"Couldn't download Listful {version}: {e.Message}");
        }
    }

    /// <summary>Closes Listful, installs the staged update and starts the new version.</summary>
    public void RestartToUpdate(Action saveFirst)
    {
        if (manager is null || pending is null || State is not UpdateState.Ready) return;
        saveFirst();
        manager.ApplyUpdatesAndRestart(pending.TargetFullRelease, Environment.GetCommandLineArgs().Skip(1).ToArray());
    }
}
