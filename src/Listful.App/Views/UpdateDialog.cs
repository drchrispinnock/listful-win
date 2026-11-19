using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Listful.App.Services;

namespace Listful.App.Views;

/// <summary>
/// Windows-only: Help ▸ Check for Updates… and the offer of an update the daily check found, where
/// Sparkle shows its own window on the Mac. Checks when opened (unless an update is already found
/// or downloading), then offers Download and Install, shows progress, and Restart to Update.
/// </summary>
internal sealed partial class UpdateDialog : ContentDialog
{
    private readonly UpdaterService updater;
    private readonly Action saveFirst;
    private readonly StackPanel body = new() { Spacing = 10, MinWidth = 380 };

    public UpdateDialog(UpdaterService updater, Action saveFirst)
    {
        this.updater = updater;
        this.saveFirst = saveFirst;
        Title = "Software Update";
        Content = body;
        PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            switch (updater.State)
            {
                case UpdaterService.UpdateState.Available:
                    var deferral = args.GetDeferral();
                    try { await updater.Download(); }
                    finally { deferral.Complete(); }
                    break;
                case UpdaterService.UpdateState.Ready:
                    updater.RestartToUpdate(saveFirst);
                    break;
            }
        };
        // A download carries on if the dialog is closed.
        updater.StateChanged += Changed;
        Closed += (_, _) => updater.StateChanged -= Changed;
        Opened += async (_, _) =>
        {
            if (updater.State is UpdaterService.UpdateState.Idle or UpdaterService.UpdateState.UpToDate or UpdaterService.UpdateState.Failed)
                await updater.Check();
        };
        Render();
    }

    private void Changed() => DispatcherQueue.TryEnqueue(Render);

    private static TextBlock Text(string text, bool secondary = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = secondary ? (Style)Application.Current.Resources["SecondaryText"] : null,
    };

    private void Render()
    {
        body.Children.Clear();
        PrimaryButtonText = "";
        CloseButtonText = "Done";
        if (!updater.IsAvailable)
        {
            body.Children.Add(Text($"This is Listful {updater.CurrentVersion}. Updates work in the installed copy of Listful "
                + "(the Setup or Portable download); this copy wasn't installed that way."));
        }
        else
        {
            switch (updater.State)
            {
                case UpdaterService.UpdateState.Idle or UpdaterService.UpdateState.Checking:
                    var checking = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    checking.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
                    checking.Children.Add(Text("Checking for updates…"));
                    body.Children.Add(checking);
                    break;
                case UpdaterService.UpdateState.UpToDate:
                    body.Children.Add(Text($"Listful {updater.CurrentVersion} is the latest version."));
                    break;
                case UpdaterService.UpdateState.Available available:
                    body.Children.Add(Text($"Listful {available.Version} is available. You have {updater.CurrentVersion}."));
                    body.Children.Add(Text("It's downloaded now and installed when Listful restarts. Your task lists stay where they are.", secondary: true));
                    (PrimaryButtonText, CloseButtonText) = ("Download and Install", "Later");
                    break;
                case UpdaterService.UpdateState.Downloading downloading:
                    body.Children.Add(Text($"Downloading Listful {downloading.Version}… {downloading.Percent}%"));
                    body.Children.Add(new ProgressBar { Value = downloading.Percent, Maximum = 100 });
                    CloseButtonText = "Hide";
                    break;
                case UpdaterService.UpdateState.Ready ready:
                    body.Children.Add(Text($"Listful {ready.Version} is ready to install."));
                    body.Children.Add(Text("Listful saves your lists, closes, installs it and opens again.", secondary: true));
                    (PrimaryButtonText, CloseButtonText) = ("Restart to Update", "Later");
                    break;
                case UpdaterService.UpdateState.Failed failed:
                    body.Children.Add(new TextBlock
                    {
                        Text = failed.Message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                    });
                    break;
            }
        }
        DefaultButton = PrimaryButtonText.Length > 0 ? ContentDialogButton.Primary : ContentDialogButton.Close;
    }
}
