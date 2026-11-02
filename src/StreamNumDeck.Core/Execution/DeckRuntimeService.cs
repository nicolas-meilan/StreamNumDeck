using System.Collections.Immutable;
using StreamNumDeck.Core.Actions;
using StreamNumDeck.Core.Configuration;
using StreamNumDeck.Core.Deck;
using StreamNumDeck.Core.Input;
using StreamNumDeck.Core.Settings;

namespace StreamNumDeck.Core.Execution;

public sealed class DeckRuntimeService : IAsyncDisposable
{
    private static readonly TimeSpan AdditionalButtonSequenceTimeout = TimeSpan.FromMilliseconds(50);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim additionalInputGate = new(1, 1);
    private readonly IReadOnlyDictionary<DeckKey, SemaphoreSlim> actionGates =
        DeckKeyCatalog.AssignableKeys.ToDictionary(static key => key, static _ => new SemaphoreSlim(1, 1));
    private readonly object runningActionsGate = new();
    private readonly object additionalKeySequenceGate = new();
    private readonly HashSet<Task> runningActions = new();
    private readonly HashSet<AdditionalKeyIdentity> pressedAdditionalKeys = [];
    private readonly List<uint> pendingAdditionalKeys = [];
    private readonly List<CapturedKeyPress?> pendingAdditionalKeyPresses = [];
    private IReadOnlyList<AdditionalButtonSettings> pendingAdditionalButtons = [];
    private string? pendingAdditionalInputDeviceId;
    private CancellationTokenSource? processingCancellation;
    private CancellationTokenSource? additionalKeySequenceTimeout;
    private Task? processingTask;
    private readonly ConfigurationService configurationService;
    private readonly IKeyboardCaptureService keyboardCapture;
    private readonly ActionDispatcher actionDispatcher;
    private EventHandler<KeyboardCaptureErrorEventArgs>? captureError;
    private ImmutableDictionary<DeckKey, string> deviceSpecificKeyboardIds =
        ImmutableDictionary<DeckKey, string>.Empty;
    private ImmutableHashSet<DeckKey> anyKeyboardKeys = ImmutableHashSet<DeckKey>.Empty;
    private bool disposed;
    private int additionalKeyCaptureMode;

    public DeckRuntimeService(
        ConfigurationService configurationService,
        IKeyboardCaptureService keyboardCapture,
        ActionDispatcher actionDispatcher)
    {
        this.configurationService = Guard.NotNull(configurationService, nameof(configurationService));
        this.keyboardCapture = Guard.NotNull(keyboardCapture, nameof(keyboardCapture));
        this.actionDispatcher = Guard.NotNull(actionDispatcher, nameof(actionDispatcher));
        this.configurationService.Changed += ConfigurationService_Changed;
        this.keyboardCapture.NumLockStateChanged += KeyboardCapture_NumLockStateChanged;
        this.keyboardCapture.CaptureError += KeyboardCapture_CaptureError;
    }

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
        add => captureError += value;
        remove => captureError -= value;
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

    public Task SetNumLockBehaviorAsync(
        NumLockBehavior behavior,
        CancellationToken cancellationToken = default) =>
        keyboardCapture.SetNumLockBehaviorAsync(behavior, cancellationToken);

    public Task<IReadOnlyList<KeyboardDeviceInfo>> GetAvailableKeyboardsAsync(
        CancellationToken cancellationToken = default) =>
        keyboardCapture.GetAvailableKeyboardsAsync(cancellationToken);

    public Task SetSelectedKeyboardDeviceAsync(
        string? deviceId,
        CancellationToken cancellationToken = default) =>
        keyboardCapture.SetSelectedKeyboardDeviceAsync(deviceId, cancellationToken);

    public Task SetKeyboardRoutedKeysAsync(
        IReadOnlyCollection<DeckKey> deviceSpecificKeys,
        IReadOnlyCollection<DeckKey> anyKeyboardKeys,
        CancellationToken cancellationToken = default) =>
        keyboardCapture.SetKeyboardRoutedKeysAsync(deviceSpecificKeys, anyKeyboardKeys, cancellationToken);

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

            var configuration = await configurationService.GetAsync(cancellationToken).ConfigureAwait(false);
            await UpdateDeviceSpecificKeysAsync(configuration, cancellationToken).ConfigureAwait(false);
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
        configurationService.Changed -= ConfigurationService_Changed;
        keyboardCapture.NumLockStateChanged -= KeyboardCapture_NumLockStateChanged;
        keyboardCapture.CaptureError -= KeyboardCapture_CaptureError;
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
                        && IsAdditionalSequenceKey(
                            profile.AdditionalButtons,
                            virtualKeyCode,
                            keyPress.DeviceId,
                            keyboardCapture.SelectedKeyboardDeviceId))
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
        if (cancellation is null)
        {
            return;
        }

        try
        {
            if (await HandleAdditionalButtonInputAsync(
                e.VirtualKeyCode,
                e.IsKeyDown,
                e.CapturedKeyPress,
                e.DeviceId,
                e.IsSelectedDevice,
                allowFallbackKeyPresses: e.IsSelectedDevice,
                cancellation.Token).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ActionExecutionFailed?.Invoke(this, new ActionExecutionFailedEventArgs(new NoActionDefinition(), exception));
            return;
        }

        if (e.CapturedKeyPress is CapturedKeyPress keyPress
            && e.IsKeyDown
            && !e.IsRepeat)
        {
            var specificKeyboardIds = Volatile.Read(ref deviceSpecificKeyboardIds);
            if (specificKeyboardIds.TryGetValue(keyPress.Key, out var selectedDeviceId))
            {
                if (!string.Equals(selectedDeviceId, e.DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                try
                {
                    var configuration = await configurationService.GetAsync(cancellation.Token).ConfigureAwait(false);
                    var assignment = configuration.ActiveProfile
                        .GetLayer(keyPress.Layer)
                        .GetAssignment(keyPress.Key);
                    if (string.Equals(
                        assignment.KeyboardDeviceId,
                        e.DeviceId,
                        StringComparison.OrdinalIgnoreCase)
                        && keyboardCapture.State is KeyboardCaptureState.Running
                        && keyboardCapture.CaptureTargets.Includes(keyPress.Key)
                        && assignment.Action is not NoActionDefinition)
                    {
                        TrackAction(ExecuteActionAsync(keyPress.Key, assignment.Action, cancellation.Token));
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    ActionExecutionFailed?.Invoke(
                        this,
                        new ActionExecutionFailedEventArgs(new NoActionDefinition(), exception));
                    return;
                }

                return;
            }

            if (Volatile.Read(ref anyKeyboardKeys).Contains(keyPress.Key))
            {
                if (keyboardCapture.State is KeyboardCaptureState.Running
                    && keyboardCapture.CaptureTargets.Includes(keyPress.Key))
                {
                    try
                    {
                        var configuration = await configurationService.GetAsync(cancellation.Token).ConfigureAwait(false);
                        var assignment = configuration.ActiveProfile
                            .GetLayer(keyPress.Layer)
                            .GetAssignment(keyPress.Key);
                        if (assignment.UseAnyKeyboard && assignment.Action is not NoActionDefinition)
                        {
                            TrackAction(ExecuteActionAsync(keyPress.Key, assignment.Action, cancellation.Token));
                        }
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        ActionExecutionFailed?.Invoke(
                            this,
                            new ActionExecutionFailedEventArgs(new NoActionDefinition(), exception));
                        return;
                    }
                }

                return;
            }
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
                deviceId: null,
                isSelectedDevice: true,
                allowFallbackKeyPresses: true,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ActionExecutionFailed?.Invoke(this, new ActionExecutionFailedEventArgs(new NoActionDefinition(), exception));
        }
    }

    private async void ConfigurationService_Changed(object? sender, ConfigurationChangedEventArgs e)
    {
        try
        {
            await UpdateDeviceSpecificKeysAsync(e.Configuration).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            captureError?.Invoke(this, new KeyboardCaptureErrorEventArgs(exception));
        }
    }

    private async void KeyboardCapture_NumLockStateChanged(object? sender, NumLockStateChangedEventArgs e)
    {
        try
        {
            var configuration = await configurationService.GetAsync().ConfigureAwait(false);
            await UpdateDeviceSpecificKeysAsync(configuration).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            captureError?.Invoke(this, new KeyboardCaptureErrorEventArgs(exception));
        }
    }

    private void KeyboardCapture_CaptureError(object? sender, KeyboardCaptureErrorEventArgs e) =>
        captureError?.Invoke(this, e);

    private async Task UpdateDeviceSpecificKeysAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await keyboardCapture.SetNumLockBehaviorAsync(
            configuration.Settings.NumLockBehavior,
            cancellationToken).ConfigureAwait(false);
        var layer = configuration.Settings.NumLockBehavior is NumLockBehavior.ActionKey || keyboardCapture.IsNumLockOn
            ? NumLockLayer.On
            : NumLockLayer.Off;
        var mappings = configuration.ActiveProfile
            .GetLayer(layer)
            .Assignments
            .Where(static assignment => !string.IsNullOrWhiteSpace(assignment.Value.KeyboardDeviceId))
            .ToImmutableDictionary(static assignment => assignment.Key, static assignment => assignment.Value.KeyboardDeviceId!);
        var anyKeys = configuration.ActiveProfile
            .GetLayer(layer)
            .Assignments
            .Where(static assignment => assignment.Value.UseAnyKeyboard)
            .Select(static assignment => assignment.Key)
            .ToImmutableHashSet();
        Volatile.Write(ref deviceSpecificKeyboardIds, mappings);
        Volatile.Write(ref anyKeyboardKeys, anyKeys);
        await keyboardCapture.SetKeyboardRoutedKeysAsync(
            mappings.Keys.ToArray(),
            anyKeys.ToArray(),
            cancellationToken).ConfigureAwait(false);
        var additionalButtonCaptureRules = configuration.ActiveProfile.AdditionalButtons
            .Where(static button => button.HasKeySequence && button.Assignment.Action is not NoActionDefinition)
            .SelectMany(static button => button.GetKeySequence()
                .Select(virtualKeyCode => new AdditionalButtonCaptureRule(
                    virtualKeyCode,
                    button.NumLockMode,
                    CaptureGlobally: string.IsNullOrWhiteSpace(button.Assignment.KeyboardDeviceId))))
            .ToArray();
        await keyboardCapture.SetAdditionalButtonCaptureRulesAsync(
            additionalButtonCaptureRules,
            cancellationToken).ConfigureAwait(false);
        var hasDeviceSpecificAdditionalButtons = configuration.ActiveProfile.AdditionalButtons.Any(
            static button => button.HasKeySequence
                && !string.IsNullOrWhiteSpace(button.Assignment.KeyboardDeviceId));
        await keyboardCapture.SetAdditionalButtonDeviceRoutingAsync(
            hasDeviceSpecificAdditionalButtons,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HandleAdditionalButtonInputAsync(
        uint virtualKeyCode,
        bool isKeyDown,
        CapturedKeyPress? capturedKeyPress,
        string? deviceId,
        bool isSelectedDevice,
        bool allowFallbackKeyPresses,
        CancellationToken cancellationToken)
    {
        List<AdditionalButtonSettings> matches = [];
        List<CapturedKeyPress> fallbackKeyPresses = [];
        IReadOnlyList<AdditionalButtonSettings> buttons;
        CancellationTokenSource? timeoutSource = null;
        var isAdditionalSequenceKey = false;
        var inputIdentity = new AdditionalKeyIdentity(deviceId, virtualKeyCode);

        if (!isKeyDown)
        {
            await additionalInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (additionalKeySequenceGate)
                {
                    pressedAdditionalKeys.Remove(inputIdentity);
                }
            }
            finally
            {
                additionalInputGate.Release();
            }

            return false;
        }

        var configuration = await configurationService.GetAsync(cancellationToken).ConfigureAwait(false);
        var configuredButtons = configuration.ActiveProfile.AdditionalButtons
            .Where(button => button.HasKeySequence
                && (button.Assignment.KeyboardDeviceId is string targetDeviceId
                    ? deviceId is not null && string.Equals(
                        targetDeviceId,
                        deviceId,
                        StringComparison.OrdinalIgnoreCase)
                    : button.Assignment.UseAnyKeyboard
                        ? deviceId is null
                            ? keyboardCapture.SelectedKeyboardDeviceId is null
                            : keyboardCapture.SelectedKeyboardDeviceId is not null
                        : isSelectedDevice))
            .ToArray();
        if (!isSelectedDevice && configuredButtons.Length == 0)
        {
            return false;
        }

        isAdditionalSequenceKey = IsAdditionalSequenceKey(
            configuredButtons,
            virtualKeyCode,
            deviceId,
            keyboardCapture.SelectedKeyboardDeviceId);
        buttons = configuredButtons
            .Where(button => IsNumLockModeActive(button.NumLockMode, keyboardCapture.IsNumLockOn))
            .ToArray();
        if (isAdditionalSequenceKey
            && !IsAdditionalSequenceKey(buttons, virtualKeyCode, deviceId, keyboardCapture.SelectedKeyboardDeviceId))
        {
            return true;
        }

        await additionalInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (additionalKeySequenceGate)
            {
                if (!pressedAdditionalKeys.Add(inputIdentity)
                    || !keyboardCapture.CaptureTargets.Includes(KeyboardCaptureTargets.Numpad)
                    || Volatile.Read(ref additionalKeyCaptureMode) != 0
                    || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                CancelAdditionalKeySequenceTimeout();
            }
            lock (additionalKeySequenceGate)
            {
                if (pendingAdditionalKeys.Count > 0
                    && !string.Equals(
                        pendingAdditionalInputDeviceId,
                        deviceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var completedPrefix = FindLongestCompletedPrefix(
                        pendingAdditionalButtons,
                        pendingAdditionalKeys);
                    var consumedKeyCount = completedPrefix?.Length ?? 0;
                    if (completedPrefix is not null)
                    {
                        matches.Add(completedPrefix.Value.Button);
                    }

                    for (var index = consumedKeyCount; index < pendingAdditionalKeyPresses.Count; index++)
                    {
                        if (pendingAdditionalKeyPresses[index] is CapturedKeyPress fallbackKeyPress)
                        {
                            fallbackKeyPresses.Add(fallbackKeyPress);
                        }
                    }

                    pendingAdditionalKeys.Clear();
                    pendingAdditionalKeyPresses.Clear();
                    pendingAdditionalButtons = [];
                    pendingAdditionalInputDeviceId = null;
                    CancelAdditionalKeySequenceTimeout();
                }

                if (pendingAdditionalKeys.Count == 0)
                {
                    pendingAdditionalInputDeviceId = deviceId;
                    pendingAdditionalButtons = buttons;
                }

                pendingAdditionalKeys.Add(virtualKeyCode);
                pendingAdditionalKeyPresses.Add(
                    capturedKeyPress is CapturedKeyPress keyPress
                        && allowFallbackKeyPresses
                        && isAdditionalSequenceKey
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
                        pendingAdditionalButtons = [];
                        pendingAdditionalInputDeviceId = null;
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
                        if (allowFallbackKeyPresses
                            && pendingAdditionalKeyPresses[0] is CapturedKeyPress fallbackKeyPress)
                        {
                            fallbackKeyPresses.Add(fallbackKeyPress);
                        }

                        pendingAdditionalKeys.RemoveAt(0);
                        pendingAdditionalKeyPresses.RemoveAt(0);
                    }
                }

                if (pendingAdditionalKeys.Count == 0)
                {
                    pendingAdditionalButtons = [];
                    pendingAdditionalInputDeviceId = null;
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

        return isAdditionalSequenceKey;
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
                pendingAdditionalButtons = [];
                pendingAdditionalInputDeviceId = null;
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
        uint virtualKeyCode,
        string? deviceId,
        string? selectedKeyboardDeviceId) =>
        buttons.Any(button =>
            button.HasKeySequence
            && button.Assignment.Action is not NoActionDefinition
            && (button.Assignment.UseAnyKeyboard
                || (button.Assignment.KeyboardDeviceId is string targetDeviceId
                    ? string.Equals(targetDeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(selectedKeyboardDeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
            && button.GetKeySequence().Contains(virtualKeyCode));

    private static bool IsNumLockModeActive(AdditionalButtonNumLockMode mode, bool isNumLockOn) =>
        mode switch
        {
            AdditionalButtonNumLockMode.Ignore => true,
            AdditionalButtonNumLockMode.Off => !isNumLockOn,
            AdditionalButtonNumLockMode.On => isNumLockOn,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

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
            pendingAdditionalButtons = [];
            pendingAdditionalInputDeviceId = null;
            pressedAdditionalKeys.Clear();
            CancelAdditionalKeySequenceTimeout();
        }
    }

    private void CancelAdditionalKeySequenceTimeout()
    {
        lock (additionalKeySequenceGate)
        {
            var timeoutSource = additionalKeySequenceTimeout;
            additionalKeySequenceTimeout = null;
            timeoutSource?.Cancel();
        }
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

    private readonly record struct AdditionalKeyIdentity(string? DeviceId, uint VirtualKeyCode);
}
