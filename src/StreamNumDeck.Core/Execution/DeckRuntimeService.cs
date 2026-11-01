using StreamNumDeck.Core.Actions;
using StreamNumDeck.Core.Configuration;
using StreamNumDeck.Core.Deck;
using StreamNumDeck.Core.Input;

namespace StreamNumDeck.Core.Execution;

public sealed class DeckRuntimeService(
    ConfigurationService configurationService,
    IKeyboardCaptureService keyboardCapture,
    ActionDispatcher actionDispatcher) : IAsyncDisposable
{
    private static readonly TimeSpan AdditionalButtonSequenceTimeout = TimeSpan.FromMilliseconds(50);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim additionalInputGate = new(1, 1);
    private readonly IReadOnlyDictionary<DeckKey, SemaphoreSlim> actionGates =
        DeckKeyCatalog.AssignableKeys.ToDictionary(static key => key, static _ => new SemaphoreSlim(1, 1));
    private readonly object runningActionsGate = new();
    private readonly object additionalKeySequenceGate = new();
    private readonly HashSet<Task> runningActions = new();
    private readonly HashSet<uint> pressedAdditionalKeys = [];
    private readonly List<uint> pendingAdditionalKeys = [];
    private readonly List<CapturedKeyPress?> pendingAdditionalKeyPresses = [];
    private CancellationTokenSource? processingCancellation;
    private CancellationTokenSource? additionalKeySequenceTimeout;
    private Task? processingTask;
    private bool disposed;
    private int additionalKeyCaptureMode;

    public KeyboardCaptureState CaptureState => keyboardCapture.State;

    public bool IsNumLockOn => keyboardCapture.IsNumLockOn;

    public KeyboardCaptureTargets CaptureTargets => keyboardCapture.CaptureTargets;

    public event EventHandler<KeyboardCaptureStateChangedEventArgs>? CaptureStateChanged
    {
        add => keyboardCapture.StateChanged += value;
        remove => keyboardCapture.StateChanged -= value;
    }

    public event EventHandler<NumLockStateChangedEventArgs>? NumLockStateChanged
    {
        add => keyboardCapture.NumLockStateChanged += value;
        remove => keyboardCapture.NumLockStateChanged -= value;
    }

    public event EventHandler<KeyboardInputObservedEventArgs>? KeyboardInputObserved
    {
        add => keyboardCapture.KeyboardInputObserved += value;
        remove => keyboardCapture.KeyboardInputObserved -= value;
    }

    public event EventHandler<RawKeyboardInputObservedEventArgs>? RawKeyboardInputObserved
    {
        add => keyboardCapture.RawKeyboardInputObserved += value;
        remove => keyboardCapture.RawKeyboardInputObserved -= value;
    }

    public event EventHandler<KeyboardCaptureErrorEventArgs>? CaptureError
    {
        add => keyboardCapture.CaptureError += value;
        remove => keyboardCapture.CaptureError -= value;
    }

    public event EventHandler<ActionExecutionFailedEventArgs>? ActionExecutionFailed;

    public void SetAdditionalKeyCaptureMode(bool isCapturing) =>
        Volatile.Write(ref additionalKeyCaptureMode, isCapturing ? 1 : 0);


    public Task SetNumLockAsync(bool isOn, CancellationToken cancellationToken = default) =>
        keyboardCapture.SetNumLockAsync(isOn, cancellationToken);

    public Task SetCaptureTargetsAsync(
        KeyboardCaptureTargets targets,
        CancellationToken cancellationToken = default) =>
        keyboardCapture.SetCaptureTargetsAsync(targets, cancellationToken);

    public Task<IReadOnlyList<KeyboardDeviceInfo>> GetAvailableKeyboardsAsync(
        CancellationToken cancellationToken = default) =>
        keyboardCapture.GetAvailableKeyboardsAsync(cancellationToken);

    public Task SetSelectedKeyboardDeviceAsync(
        string? deviceId,
        CancellationToken cancellationToken = default) =>
        keyboardCapture.SetSelectedKeyboardDeviceAsync(deviceId, cancellationToken);

    public Task SetDeviceIdentificationKeysAsync(
        IReadOnlyCollection<DeckKey> numLockOffKeys,
        IReadOnlyCollection<DeckKey> numLockOnKeys,
        CancellationToken cancellationToken = default) =>
        keyboardCapture.SetDeviceIdentificationKeysAsync(numLockOffKeys, numLockOnKeys, cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(disposed, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (processingTask is not null)
            {
                return;
            }

            await keyboardCapture.StartAsync(cancellationToken).ConfigureAwait(false);
            keyboardCapture.KeyboardInputObserved += KeyboardCapture_KeyboardInputObserved;
            keyboardCapture.RawKeyboardInputObserved += KeyboardCapture_RawKeyboardInputObserved;
            processingCancellation = new CancellationTokenSource();
            processingTask = ProcessInputAsync(processingCancellation.Token);
        }
        catch
        {
            await keyboardCapture.StopAsync(CancellationToken.None).ConfigureAwait(false);
            processingCancellation?.Dispose();
            processingCancellation = null;
            processingTask = null;
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (processingTask is null)
            {
                return;
            }

            processingCancellation!.Cancel();
            CancelPendingAdditionalKeySequence();
            keyboardCapture.KeyboardInputObserved -= KeyboardCapture_KeyboardInputObserved;
            keyboardCapture.RawKeyboardInputObserved -= KeyboardCapture_RawKeyboardInputObserved;
            await keyboardCapture.StopAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
            {
            }

            await additionalInputGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            additionalInputGate.Release();
            processingCancellation.Dispose();
            processingCancellation = null;
            processingTask = null;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        await keyboardCapture.DisposeAsync().ConfigureAwait(false);
        foreach (var gate in actionGates.Values)
        {
            gate.Dispose();
        }

        lifecycleGate.Dispose();
        disposed = true;
    }

    private async Task ProcessInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var keyPress in keyboardCapture.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                ActionDefinition action;
                try
                {
                    var configuration = await configurationService.GetAsync(cancellationToken).ConfigureAwait(false);
                    var profile = configuration.Profiles.Single(profile => profile.Id == configuration.ActiveProfileId);
                    if (keyPress.VirtualKeyCode is uint virtualKeyCode
                        && IsAdditionalSequenceKey(profile.AdditionalButtons, virtualKeyCode))
                    {
                        continue;
                    }

                    action = profile.GetLayer(keyPress.Layer).GetAssignment(keyPress.Key).Action;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    ActionExecutionFailed?.Invoke(this, new ActionExecutionFailedEventArgs(new NoActionDefinition(), exception));
                    continue;
                }

                if (action is NoActionDefinition)
                {
                    continue;
                }

                TrackAction(ExecuteActionAsync(keyPress.Key, action, cancellationToken));
            }
        }
        finally
        {
            Task[] pendingActions;
            lock (runningActionsGate)
            {
                pendingActions = runningActions.ToArray();
            }

            await Task.WhenAll(pendingActions).ConfigureAwait(false);
        }
    }

    private async void KeyboardCapture_RawKeyboardInputObserved(object? sender, RawKeyboardInputObservedEventArgs e)
    {
        var cancellation = processingCancellation;
        if (keyboardCapture.SelectedKeyboardDeviceId is null || !e.IsSelectedDevice || cancellation is null)
        {
            return;
        }

        try
        {
            await HandleAdditionalButtonInputAsync(
                e.VirtualKeyCode,
                e.IsKeyDown,
                e.CapturedKeyPress,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ActionExecutionFailed?.Invoke(this, new ActionExecutionFailedEventArgs(new NoActionDefinition(), exception));
        }
    }

    private async void KeyboardCapture_KeyboardInputObserved(object? sender, KeyboardInputObservedEventArgs e)
    {
        var cancellation = processingCancellation;
        if (keyboardCapture.SelectedKeyboardDeviceId is not null || cancellation is null)
        {
            return;
        }

        try
        {
            await HandleAdditionalButtonInputAsync(
                e.VirtualKeyCode,
                e.IsKeyDown,
                e.CapturedKeyPress,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ActionExecutionFailed?.Invoke(this, new ActionExecutionFailedEventArgs(new NoActionDefinition(), exception));
        }
    }

    private async Task HandleAdditionalButtonInputAsync(
        uint virtualKeyCode,
        bool isKeyDown,
        CapturedKeyPress? capturedKeyPress,
        CancellationToken cancellationToken)
    {
        List<AdditionalButtonSettings> matches = [];
        List<CapturedKeyPress> fallbackKeyPresses = [];
        IReadOnlyList<AdditionalButtonSettings> buttons = [];
        CancellationTokenSource? timeoutSource = null;
        await additionalInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (additionalKeySequenceGate)
            {
                if (!isKeyDown)
                {
                    pressedAdditionalKeys.Remove(virtualKeyCode);
                }
                else
                {
                    if (!pressedAdditionalKeys.Add(virtualKeyCode)
                        || !keyboardCapture.CaptureTargets.Includes(KeyboardCaptureTargets.Numpad)
                        || Volatile.Read(ref additionalKeyCaptureMode) != 0
                        || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    CancelAdditionalKeySequenceTimeout();
                }
            }

            if (!isKeyDown)
            {
                return;
            }

            var configuration = await configurationService.GetAsync(cancellationToken).ConfigureAwait(false);
            buttons = configuration.ActiveProfile.AdditionalButtons
                .Where(static button => button.HasKeySequence)
                .ToArray();
            lock (additionalKeySequenceGate)
            {
                pendingAdditionalKeys.Add(virtualKeyCode);
                pendingAdditionalKeyPresses.Add(
                    capturedKeyPress is CapturedKeyPress keyPress
                        && IsAdditionalSequenceKey(buttons, virtualKeyCode)
                            ? keyPress
                            : null);

                while (pendingAdditionalKeys.Count > 0)
                {
                    var matchingButton = FindButtonMatchingPrefix(buttons, pendingAdditionalKeys);
                    if (matchingButton is not null)
                    {
                        if (HasLongerButtonMatchingPrefix(buttons, pendingAdditionalKeys))
                        {
                            ScheduleAdditionalKeySequenceTimeout(cancellationToken);
                            break;
                        }

                        matches.Add(matchingButton);
                        pendingAdditionalKeys.Clear();
                        pendingAdditionalKeyPresses.Clear();
                        CancelAdditionalKeySequenceTimeout();
                        break;
                    }

                    var completedPrefix = FindLongestCompletedPrefix(buttons, pendingAdditionalKeys);
                    if (completedPrefix is not null)
                    {
                        matches.Add(completedPrefix.Value.Button);
                        pendingAdditionalKeys.RemoveRange(0, completedPrefix.Value.Length);
                        pendingAdditionalKeyPresses.RemoveRange(0, completedPrefix.Value.Length);
                    }
                    else
                    {
                        if (pendingAdditionalKeyPresses[0] is CapturedKeyPress fallbackKeyPress)
                        {
                            fallbackKeyPresses.Add(fallbackKeyPress);
                        }

                        pendingAdditionalKeys.RemoveAt(0);
                        pendingAdditionalKeyPresses.RemoveAt(0);
                    }
                }

                if (pendingAdditionalKeys.Count == 0)
                {
                    CancelAdditionalKeySequenceTimeout();
                }

                timeoutSource = additionalKeySequenceTimeout;
            }

        }
        finally
        {
            additionalInputGate.Release();
        }

        if (timeoutSource is not null)
        {
            _ = FlushAdditionalKeySequenceAfterTimeoutAsync(
                timeoutSource,
                buttons,
                cancellationToken);
        }

        foreach (var keyPress in fallbackKeyPresses)
        {
            await ExecuteNormalKeyPressAsync(keyPress, cancellationToken).ConfigureAwait(false);
        }

        foreach (var button in matches)
        {
            await ExecuteAdditionalButtonAsync(button.Slot, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushAdditionalKeySequenceAfterTimeoutAsync(
        CancellationTokenSource timeoutSource,
        IReadOnlyList<AdditionalButtonSettings> buttons,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AdditionalButtonSequenceTimeout, timeoutSource.Token).ConfigureAwait(false);
            AdditionalButtonSettings? match;
            List<CapturedKeyPress> fallbackKeyPresses = [];
            lock (additionalKeySequenceGate)
            {
                if (!ReferenceEquals(additionalKeySequenceTimeout, timeoutSource))
                {
                    return;
                }

                additionalKeySequenceTimeout = null;
                var completedPrefix = FindLongestCompletedPrefix(buttons, pendingAdditionalKeys);
                match = completedPrefix?.Button;
                var consumedKeyCount = completedPrefix?.Length ?? 0;
                for (var index = consumedKeyCount; index < pendingAdditionalKeyPresses.Count; index++)
                {
                    if (pendingAdditionalKeyPresses[index] is CapturedKeyPress keyPress)
                    {
                        fallbackKeyPresses.Add(keyPress);
                    }
                }

                pendingAdditionalKeys.Clear();
                pendingAdditionalKeyPresses.Clear();
            }

            if (match is not null)
            {
                await ExecuteAdditionalButtonAsync(match.Slot, cancellationToken).ConfigureAwait(false);
            }

            foreach (var keyPress in fallbackKeyPresses)
            {
                await ExecuteNormalKeyPressAsync(keyPress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ActionExecutionFailed?.Invoke(this, new ActionExecutionFailedEventArgs(new NoActionDefinition(), exception));
        }
        finally
        {
            timeoutSource.Dispose();
        }
    }

    private static AdditionalButtonSettings? FindButtonMatchingPrefix(
        IReadOnlyList<AdditionalButtonSettings> buttons,
        IReadOnlyList<uint> keys) =>
        buttons.FirstOrDefault(button =>
        {
            var sequence = button.GetKeySequence();
            return sequence.Length >= keys.Count
                && keys.Select((key, index) => sequence[index] == key).All(static matches => matches);
        });

    private static bool IsAdditionalSequenceKey(
        IReadOnlyList<AdditionalButtonSettings> buttons,
        uint virtualKeyCode) =>
        buttons.Any(button =>
            button.HasKeySequence
            && button.Assignment.Action is not NoActionDefinition
            && button.GetKeySequence().Contains(virtualKeyCode));

    private static bool HasLongerButtonMatchingPrefix(
        IReadOnlyList<AdditionalButtonSettings> buttons,
        IReadOnlyList<uint> keys) =>
        buttons.Any(button =>
        {
            var sequence = button.GetKeySequence();
            return sequence.Length > keys.Count
                && keys.Select((key, index) => sequence[index] == key).All(static matches => matches);
        });

    private static (AdditionalButtonSettings Button, int Length)? FindLongestCompletedPrefix(
        IReadOnlyList<AdditionalButtonSettings> buttons,
        IReadOnlyList<uint> keys)
    {
        for (var length = Math.Min(keys.Count, AdditionalButtonSettings.MaximumKeySequenceLength);
            length > 0;
            length--)
        {
            var button = buttons.FirstOrDefault(candidate =>
            {
                var sequence = candidate.GetKeySequence();
                return sequence.Length == length
                    && sequence.Select((key, index) => key == keys[index]).All(static matches => matches);
            });
            if (button is not null)
            {
                return (button, length);
            }
        }

        return null;
    }

    private void ScheduleAdditionalKeySequenceTimeout(CancellationToken cancellationToken)
    {
        CancelAdditionalKeySequenceTimeout();
        additionalKeySequenceTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    private void CancelPendingAdditionalKeySequence()
    {
        lock (additionalKeySequenceGate)
        {
            pendingAdditionalKeys.Clear();
            pendingAdditionalKeyPresses.Clear();
            pressedAdditionalKeys.Clear();
            CancelAdditionalKeySequenceTimeout();
        }
    }

    private void CancelAdditionalKeySequenceTimeout()
    {
        var timeoutSource = additionalKeySequenceTimeout;
        additionalKeySequenceTimeout = null;
        timeoutSource?.Cancel();
    }

    private async Task ExecuteAdditionalButtonAsync(int slot, CancellationToken cancellationToken)
    {
        var configuration = await configurationService.GetAsync(cancellationToken).ConfigureAwait(false);
        var button = configuration.ActiveProfile.AdditionalButtons[slot];
        if (button.Assignment.Action is not NoActionDefinition)
        {
            await actionDispatcher.ExecuteAsync(button.Assignment.Action, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteNormalKeyPressAsync(
        CapturedKeyPress keyPress,
        CancellationToken cancellationToken)
    {
        var configuration = await configurationService.GetAsync(cancellationToken).ConfigureAwait(false);
        var profile = configuration.Profiles.Single(profile => profile.Id == configuration.ActiveProfileId);
        var action = profile.GetLayer(keyPress.Layer).GetAssignment(keyPress.Key).Action;
        if (action is not NoActionDefinition)
        {
            await ExecuteActionAsync(keyPress.Key, action, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteActionAsync(
        DeckKey key,
        ActionDefinition action,
        CancellationToken cancellationToken)
    {
        var gate = actionGates[key];
        var gateEntered = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            await actionDispatcher.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AutomationStepExecutionException exception)
        {
            ActionExecutionFailed?.Invoke(
                this,
                new ActionExecutionFailedEventArgs(
                    exception.Action,
                    exception.InnerException ?? exception));
        }
        catch (Exception exception)
        {
            ActionExecutionFailed?.Invoke(this, new ActionExecutionFailedEventArgs(action, exception));
        }
        finally
        {
            if (gateEntered)
            {
                gate.Release();
            }
        }
    }

    private void TrackAction(Task actionTask)
    {
        lock (runningActionsGate)
        {
            runningActions.Add(actionTask);
        }

        _ = actionTask.ContinueWith(
            completedTask =>
            {
                lock (runningActionsGate)
                {
                    runningActions.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
