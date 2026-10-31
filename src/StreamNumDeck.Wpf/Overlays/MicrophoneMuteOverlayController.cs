using System.Windows.Threading;
using StreamNumDeck.Core.Audio;

namespace StreamNumDeck.Wpf.Overlays;

public sealed class MicrophoneMuteOverlayController : IAsyncDisposable
{
    private readonly ISystemAudioControlService audioControlService;
    private readonly OverlayPositionStore positionStore;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };
    private MicrophoneMuteOverlayWindow? window;
    private bool refreshInProgress;
    private bool isEnabled = true;
    private DateTimeOffset previewUntil;

    public MicrophoneMuteOverlayController(
        ISystemAudioControlService audioControlService,
        OverlayPositionStore positionStore)
    {
        this.audioControlService = audioControlService;
        this.positionStore = positionStore;
        timer.Tick += Timer_Tick;
    }

    public Task StartAsync() => SetEnabledAsync(true);

    public async Task SetEnabledAsync(bool enabled)
    {
        isEnabled = enabled;
        if (!enabled)
        {
            timer.Stop();
            previewUntil = default;
            window?.Hide();
            return;
        }

        window ??= new MicrophoneMuteOverlayWindow(positionStore);
        await RefreshAsync();
        timer.Start();
    }

    public Task ShowPreviewAsync()
    {
        if (window is null)
        {
            window = new MicrophoneMuteOverlayWindow(positionStore);
        }

        previewUntil = DateTimeOffset.UtcNow.AddSeconds(10);
        window?.Show();
        if (!timer.IsEnabled)
        {
            timer.Start();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        timer.Stop();
        timer.Tick -= Timer_Tick;
        window?.Close();
        window = null;
        return default;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            await RefreshAsync();
        }
        catch
        {
            window?.Hide();
        }
    }

    private async Task RefreshAsync()
    {
        if (refreshInProgress || window is null)
        {
            return;
        }

        refreshInProgress = true;
        try
        {
            if (DateTimeOffset.UtcNow < previewUntil)
            {
                if (!window.IsVisible)
                {
                    window.Show();
                }

                return;
            }

            if (!isEnabled)
            {
                window.Hide();
                timer.Stop();
                return;
            }

            var isMicrophoneMuted = await audioControlService.GetDefaultMicrophoneMuteAsync();
            if (!isEnabled)
            {
                window.Hide();
                return;
            }

            if (isMicrophoneMuted)
            {
                if (!window.IsVisible)
                {
                    window.Show();
                }
            }
            else
            {
                window.Hide();
            }
        }
        finally
        {
            refreshInProgress = false;
        }
    }
}
