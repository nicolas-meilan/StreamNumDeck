using System.Threading.Channels;
using StreamNumDeck.Core.Actions;
using StreamNumDeck.Core.Configuration;
using StreamNumDeck.Core.Deck;
using StreamNumDeck.Core.Execution;
using StreamNumDeck.Core.Icons;
using StreamNumDeck.Core.Input;

namespace StreamNumDeck.Core.Tests;

[TestClass]
public sealed class DeckRuntimeServiceTests
{
    [TestMethod]
    public async Task DifferentKeys_CanExecuteWhileAutomationIsWaiting()
    {
        var configuration = AppConfiguration.CreateDefault();
        var profile = configuration.ActiveProfile
            .WithAssignment(
                NumLockLayer.Off,
                DeckKey.Numpad1,
                new KeyAssignment("First", IconReference.BuiltIn("play"), new OpenPathActionDefinition("first")))
            .WithAssignment(
                NumLockLayer.Off,
                DeckKey.Numpad2,
                new KeyAssignment("Second", IconReference.BuiltIn("play"), new OpenPathActionDefinition("second")));
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(configuration.ReplaceProfile(profile)));
        var capture = new TestKeyboardCaptureService();
        var executor = new BlockingExecutor();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(new[] { executor }));

        try
        {
            await runtime.StartAsync();
            capture.Publish(DeckKey.Numpad1);
            await WaitWithTimeoutAsync(executor.FirstStarted.Task);

            capture.Publish(DeckKey.Numpad2);
            await WaitWithTimeoutAsync(executor.SecondStarted.Task);

            Assert.IsFalse(executor.ReleaseFirst.Task.IsCompleted);
        }
        finally
        {
            executor.ReleaseFirst.TrySetResult(true);
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SameKey_QueuesRepeatedExecutionsWithoutOverlap()
    {
        var configuration = AppConfiguration.CreateDefault();
        var profile = configuration.ActiveProfile.WithAssignment(
            NumLockLayer.Off,
            DeckKey.Numpad1,
            new KeyAssignment("First", IconReference.BuiltIn("play"), new OpenPathActionDefinition("first")));
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(configuration.ReplaceProfile(profile)));
        var capture = new TestKeyboardCaptureService();
        var executor = new BlockingExecutor();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(new[] { executor }));

        try
        {
            await runtime.StartAsync();
            capture.Publish(DeckKey.Numpad1);
            await WaitWithTimeoutAsync(executor.FirstStarted.Task);
            capture.Publish(DeckKey.Numpad1);

            await Task.Delay(50);
            Assert.AreEqual(1, executor.StartCount);

            executor.ReleaseFirst.TrySetResult(true);
            await WaitUntilAsync(() => executor.StartCount == 2);
            Assert.AreEqual(1, executor.MaximumConcurrency);
        }
        finally
        {
            executor.ReleaseFirst.TrySetResult(true);
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AdditionalButton_CanUseBackspaceAndRespectsNumpadCaptureToggle()
    {
        var configuration = AppConfiguration.CreateDefault();
        var additional = new AdditionalButtonSettings(0, 0x08,
            new KeyAssignment("Extra", IconReference.BuiltIn("play"), new OpenPathActionDefinition("extra")));
        configuration = configuration.ReplaceProfile(configuration.ActiveProfile.WithAdditionalButton(additional));
        using var configurationService = new ConfigurationService(new InMemoryConfigurationStore(configuration));
        var capture = new TestKeyboardCaptureService();
        var executor = new BlockingExecutor();
        var runtime = new DeckRuntimeService(configurationService, capture, new ActionDispatcher(new[] { executor }));

        try
        {
            await runtime.StartAsync();
            await capture.SetCaptureTargetsAsync(KeyboardCaptureTargets.None);
            capture.PublishObservedInput(0x08, 0x0E, isExtended: false);
            capture.PublishObservedInput(0x08, 0x0E, isExtended: false, isKeyDown: false);
            await Task.Delay(100);
            Assert.AreEqual(0, executor.StartCount, "An additional key must be ignored while NumPad capture is disabled.");

            await capture.SetCaptureTargetsAsync(KeyboardCaptureTargets.Numpad);
            capture.PublishObservedInput(0x08, 0x0E, isExtended: false);
            await WaitWithTimeoutAsync(executor.FirstStarted.Task);
            capture.PublishObservedInput(0x08, 0x0E, isExtended: false, isKeyDown: false);
        }
        finally
        {
            executor.ReleaseFirst.TrySetResult(true);
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AdditionalButton_OnlyRespondsToItsSelectedKeyboard()
    {
        var configuration = AppConfiguration.CreateDefault();
        var additionalButton = new AdditionalButtonSettings(0, 0x41,
            new KeyAssignment("Extra", IconReference.BuiltIn("play"), new OpenPathActionDefinition("extra"))
            {
                KeyboardDeviceId = "additional-keyboard",
            });
        configuration = configuration.ReplaceProfile(
            configuration.ActiveProfile.WithAdditionalButton(additionalButton));
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(configuration));
        var capture = new TestKeyboardCaptureService();
        var executor = new SequenceRecordingExecutor();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(new[] { executor }));

        try
        {
            await capture.SetSelectedKeyboardDeviceAsync(null);
            await runtime.StartAsync();
            Assert.IsTrue(capture.AdditionalButtonDeviceRoutingEnabled);

            capture.PublishRawInput(
                0x41,
                isSelectedDevice: false,
                deviceId: "additional-keyboard");
            await WaitUntilAsync(() => executor.ExecutedPaths.Count == 1);
            capture.PublishRawInput(
                0x41,
                isSelectedDevice: false,
                isKeyDown: false,
                deviceId: "additional-keyboard");

            capture.PublishRawInput(0x41, isSelectedDevice: true, deviceId: "global-keyboard");
            capture.PublishRawInput(
                0x41,
                isSelectedDevice: true,
                isKeyDown: false,
                deviceId: "global-keyboard");
            await Task.Delay(100);

            CollectionAssert.AreEqual(new[] { "extra" }, executor.ExecutedPaths.ToArray());
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AdditionalButton_CanRespondToAnyKeyboard()
    {
        var configuration = AppConfiguration.CreateDefault();
        var additionalButton = new AdditionalButtonSettings(0, 0x41,
            new KeyAssignment("Extra", IconReference.BuiltIn("play"), new OpenPathActionDefinition("extra"))
            {
                UseAnyKeyboard = true,
            });
        configuration = configuration.ReplaceProfile(
            configuration.ActiveProfile.WithAdditionalButton(additionalButton));
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(configuration));
        var capture = new TestKeyboardCaptureService();
        var executor = new SequenceRecordingExecutor();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(new[] { executor }));

        try
        {
            await capture.SetSelectedKeyboardDeviceAsync("global-keyboard");
            await runtime.StartAsync();

            capture.PublishRawInput(
                0x41,
                isSelectedDevice: false,
                deviceId: "another-keyboard");
            await WaitUntilAsync(() => executor.ExecutedPaths.Count == 1);
            CollectionAssert.AreEqual(new[] { "extra" }, executor.ExecutedPaths.ToArray());
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AdditionalButtonSequences_OnlyWaitForConfiguredAmbiguousPrefixes()
    {
        var configuration = AppConfiguration.CreateDefault();
        var profile = configuration.ActiveProfile.WithAssignment(
                NumLockLayer.Off,
                DeckKey.Numpad0,
                new KeyAssignment("Numpad zero", IconReference.BuiltIn("play"), new OpenPathActionDefinition("numpad-zero")))
            .WithAdditionalButton(new AdditionalButtonSettings(0, 0x60,
                new KeyAssignment("Zero", IconReference.BuiltIn("play"), new OpenPathActionDefinition("zero"))))
            .WithAdditionalButton(new AdditionalButtonSettings(1, null,
                new KeyAssignment("Double zero", IconReference.BuiltIn("play"), new OpenPathActionDefinition("double")))
            {
                VirtualKeySequence = [0x60, 0x60],
            })
            .WithAdditionalButton(new AdditionalButtonSettings(2, 0x61,
                new KeyAssignment("One", IconReference.BuiltIn("play"), new OpenPathActionDefinition("one"))));
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(configuration.ReplaceProfile(profile)));
        var capture = new TestKeyboardCaptureService();
        var executor = new SequenceRecordingExecutor();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(new[] { executor }));

        try
        {
            await capture.SetSelectedKeyboardDeviceAsync("selected-keyboard");
            await runtime.StartAsync();

            capture.PublishRawInput(0x61, isSelectedDevice: false);
            Assert.AreEqual(0, executor.ExecutedPaths.Count, "A different keyboard must not trigger the configured button.");
            capture.PublishRawInput(0x61, isSelectedDevice: true);
            await WaitWithTimeoutAsync(executor.FirstExecution.Task);
            Assert.AreEqual("one", executor.ExecutedPaths.Single());

            capture.PublishRawInput(0x60, isSelectedDevice: true);
            await WaitUntilAsync(() => executor.ExecutedPaths.Count == 2);
            CollectionAssert.AreEqual(new[] { "one", "zero" }, executor.ExecutedPaths.ToArray());
            capture.PublishRawInput(0x60, isSelectedDevice: true, isKeyDown: false);

            capture.PublishRawInput(0x60, isSelectedDevice: true);
            capture.PublishRawInput(0x60, isSelectedDevice: true, isKeyDown: false);
            capture.PublishRawInput(0x60, isSelectedDevice: true);
            capture.PublishRawInput(0x60, isSelectedDevice: true, isKeyDown: false);
            await WaitWithTimeoutAsync(executor.ThirdExecution.Task);
            CollectionAssert.AreEqual(new[] { "one", "zero", "double" }, executor.ExecutedPaths.ToArray());
            Assert.IsFalse(executor.ExecutedPaths.Contains("numpad-zero"),
                "A key assigned to a manual button sequence must not also execute its NumPad assignment.");
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AdditionalButtonSequence_UsesNormalKeyActionWhenOnlyLongerSequenceMatches()
    {
        var configuration = AppConfiguration.CreateDefault();
        var profile = configuration.ActiveProfile
            .WithAssignment(
                NumLockLayer.Off,
                DeckKey.Numpad0,
                new KeyAssignment("Zero", IconReference.BuiltIn("play"), new OpenPathActionDefinition("zero")))
            .WithAdditionalButton(new AdditionalButtonSettings(1, null,
                new KeyAssignment("Double zero", IconReference.BuiltIn("play"), new OpenPathActionDefinition("double")))
            {
                VirtualKeySequence = [0x60, 0x60],
            });
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(configuration.ReplaceProfile(profile)));
        var capture = new TestKeyboardCaptureService();
        var executor = new SequenceRecordingExecutor();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(new[] { executor }));

        try
        {
            await capture.SetSelectedKeyboardDeviceAsync("selected-keyboard");
            await runtime.StartAsync();

            capture.PublishRawInput(0x60, isSelectedDevice: true);
            await WaitUntilAsync(() => executor.ExecutedPaths.Count == 1);
            CollectionAssert.AreEqual(new[] { "zero" }, executor.ExecutedPaths.ToArray());
            capture.PublishRawInput(0x60, isSelectedDevice: true, isKeyDown: false);

            capture.PublishRawInput(0x60, isSelectedDevice: true);
            capture.PublishRawInput(0x60, isSelectedDevice: true, isKeyDown: false);
            capture.PublishRawInput(0x60, isSelectedDevice: true);
            capture.PublishRawInput(0x60, isSelectedDevice: true, isKeyDown: false);
            await WaitUntilAsync(() => executor.ExecutedPaths.Count == 2);
            CollectionAssert.AreEqual(new[] { "zero", "double" }, executor.ExecutedPaths.ToArray());
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task KeyboardInputObserved_IsForwardedFromCaptureService()
    {
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(AppConfiguration.CreateDefault()));
        var capture = new TestKeyboardCaptureService();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(Array.Empty<IActionExecutor>()));
        KeyboardInputObservedEventArgs? observed = null;
        runtime.KeyboardInputObserved += (_, args) => observed = args;

        capture.PublishObservedInput(0x0D, 0x1C, isExtended: true);

        Assert.IsNotNull(observed);
        Assert.AreEqual(0x0Du, observed.VirtualKeyCode);
        Assert.AreEqual(0x1Cu, observed.ScanCode);
        Assert.IsTrue(observed.IsExtended);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task SetSelectedKeyboardDevice_IsForwardedToCaptureService()
    {
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(AppConfiguration.CreateDefault()));
        var capture = new TestKeyboardCaptureService();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(Array.Empty<IActionExecutor>()));

        await runtime.SetSelectedKeyboardDeviceAsync(@"\\?\HID#Keyboard#Device");

        Assert.AreEqual(@"\\?\HID#Keyboard#Device", capture.SelectedKeyboardDeviceId);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task PerKeyKeyboard_OverridesGlobalKeyboardAndRunsOnlyForMatchingDevice()
    {
        var configuration = AppConfiguration.CreateDefault();
        var assignment = new KeyAssignment(
            "Specific keyboard",
            IconReference.BuiltIn("play"),
            new OpenPathActionDefinition("specific"))
        {
            KeyboardDeviceId = "specific-keyboard",
        };
        var profile = configuration.ActiveProfile.WithAssignment(NumLockLayer.Off, DeckKey.Numpad0, assignment);
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(configuration.ReplaceProfile(profile)));
        var capture = new TestKeyboardCaptureService();
        var executor = new SequenceRecordingExecutor();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(new[] { executor }));

        try
        {
            await capture.SetSelectedKeyboardDeviceAsync("global-keyboard");
            await runtime.StartAsync();

            CollectionAssert.AreEqual(new[] { DeckKey.Numpad0 }, capture.DeviceSpecificKeys.ToArray());
            capture.PublishDeviceKey(DeckKey.Numpad0, "global-keyboard", isSelectedDevice: true);
            await Task.Delay(50);
            Assert.AreEqual(0, executor.ExecutedPaths.Count, "The per-key selection must override the global keyboard.");

            capture.PublishDeviceKey(DeckKey.Numpad0, "specific-keyboard", isSelectedDevice: false);
            await WaitUntilAsync(() => executor.ExecutedPaths.Count == 1);
            capture.PublishDeviceKey(
                DeckKey.Numpad0,
                "specific-keyboard",
                isSelectedDevice: false,
                isRepeat: true);
            await Task.Delay(50);

            CollectionAssert.AreEqual(new[] { "specific" }, executor.ExecutedPaths.ToArray());
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task PerKeyAnyKeyboard_IgnoresGlobalKeyboardSelection()
    {
        var configuration = AppConfiguration.CreateDefault();
        var assignment = new KeyAssignment(
            "Any keyboard",
            IconReference.BuiltIn("play"),
            new OpenPathActionDefinition("any"))
        {
            UseAnyKeyboard = true,
        };
        var profile = configuration.ActiveProfile.WithAssignment(NumLockLayer.Off, DeckKey.Numpad0, assignment);
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(configuration.ReplaceProfile(profile)));
        var capture = new TestKeyboardCaptureService();
        var executor = new SequenceRecordingExecutor();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(new[] { executor }));

        try
        {
            await capture.SetSelectedKeyboardDeviceAsync("global-keyboard");
            await runtime.StartAsync();

            CollectionAssert.AreEqual(new[] { DeckKey.Numpad0 }, capture.AnyKeyboardKeys.ToArray());
            capture.PublishDeviceKey(DeckKey.Numpad0, "global-keyboard", isSelectedDevice: true);
            await WaitUntilAsync(() => executor.ExecutedPaths.Count == 1);
            capture.PublishDeviceKey(DeckKey.Numpad0, "other-keyboard", isSelectedDevice: false);
            await WaitUntilAsync(() => executor.ExecutedPaths.Count == 2);

            CollectionAssert.AreEqual(new[] { "any", "any" }, executor.ExecutedPaths.ToArray());
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SetDeviceIdentificationKeys_IsForwardedToCaptureService()
    {
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(AppConfiguration.CreateDefault()));
        var capture = new TestKeyboardCaptureService();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(Array.Empty<IActionExecutor>()));

        await runtime.SetDeviceIdentificationKeysAsync(
            [DeckKey.NumpadSubtract],
            [DeckKey.Insert]);

        CollectionAssert.AreEqual(new[] { DeckKey.NumpadSubtract }, capture.DeviceIdentificationKeysOff.ToArray());
        CollectionAssert.AreEqual(new[] { DeckKey.Insert }, capture.DeviceIdentificationKeysOn.ToArray());
        await runtime.DisposeAsync();
    }

    private static async Task WaitWithTimeoutAsync(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3))) != task)
        {
            Assert.Fail("The expected runtime operation did not complete in time.");
        }

        await task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "The expected runtime state was not reached in time.");
    }

    private sealed class BlockingExecutor : IActionExecutor
    {
        private int concurrency;
        private int maximumConcurrency;
        private int startCount;

        public TaskCompletionSource<bool> FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCount => Volatile.Read(ref startCount);

        public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        public bool CanExecute(ActionDefinition action) => action is OpenPathActionDefinition;

        public async Task ExecuteAsync(ActionDefinition action, CancellationToken cancellationToken = default)
        {
            var currentConcurrency = Interlocked.Increment(ref concurrency);
            UpdateMaximumConcurrency(currentConcurrency);
            var currentStart = Interlocked.Increment(ref startCount);

            try
            {
                if (currentStart == 1)
                {
                    FirstStarted.TrySetResult(true);
                    await WaitWithCancellationAsync(ReleaseFirst.Task, cancellationToken);
                }
                else
                {
                    SecondStarted.TrySetResult(true);
                }
            }
            finally
            {
                Interlocked.Decrement(ref concurrency);
            }
        }

        private void UpdateMaximumConcurrency(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximumConcurrency);
                if (value <= current || Interlocked.CompareExchange(ref maximumConcurrency, value, current) == current)
                {
                    return;
                }
            }
        }

        private static async Task WaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancellation.TrySetCanceled()))
            {
                var completed = await Task.WhenAny(task, cancellation.Task);
                await completed;
            }
        }
    }

    private sealed class TestKeyboardCaptureService : IKeyboardCaptureService
    {
        private readonly Channel<CapturedKeyPress> channel = Channel.CreateUnbounded<CapturedKeyPress>();

        public KeyboardCaptureState State { get; private set; }

        public bool IsNumLockOn => false;

        public KeyboardCaptureTargets CaptureTargets { get; private set; } = KeyboardCaptureTargets.All;

        public string? SelectedKeyboardDeviceId { get; private set; }

        public IReadOnlyCollection<DeckKey> DeviceIdentificationKeysOff { get; private set; } = [];

        public IReadOnlyCollection<DeckKey> DeviceIdentificationKeysOn { get; private set; } = [];

        public IReadOnlyCollection<DeckKey> DeviceSpecificKeys { get; private set; } = [];

        public IReadOnlyCollection<DeckKey> AnyKeyboardKeys { get; private set; } = [];

        public bool AdditionalButtonDeviceRoutingEnabled { get; private set; }

        public event EventHandler<KeyboardCaptureStateChangedEventArgs>? StateChanged;

        public event EventHandler<NumLockStateChangedEventArgs>? NumLockStateChanged;

        public event EventHandler<KeyboardInputObservedEventArgs>? KeyboardInputObserved;

        public event EventHandler<RawKeyboardInputObservedEventArgs>? RawKeyboardInputObserved;

        public event EventHandler<KeyboardCaptureErrorEventArgs>? CaptureError
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            State = KeyboardCaptureState.Running;
            StateChanged?.Invoke(this, new KeyboardCaptureStateChangedEventArgs(State));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            State = KeyboardCaptureState.Stopped;
            StateChanged?.Invoke(this, new KeyboardCaptureStateChangedEventArgs(State));
            return Task.CompletedTask;
        }

        public Task SetCaptureTargetsAsync(
            KeyboardCaptureTargets targets,
            CancellationToken cancellationToken = default)
        {
            CaptureTargets = targets;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<KeyboardDeviceInfo>> GetAvailableKeyboardsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KeyboardDeviceInfo>>([]);

        public Task SetSelectedKeyboardDeviceAsync(
            string? deviceId,
            CancellationToken cancellationToken = default)
        {
            SelectedKeyboardDeviceId = deviceId;
            return Task.CompletedTask;
        }

        public Task SetKeyboardRoutedKeysAsync(
            IReadOnlyCollection<DeckKey> deviceSpecificKeys,
            IReadOnlyCollection<DeckKey> anyKeyboardKeys,
            CancellationToken cancellationToken = default)
        {
            DeviceSpecificKeys = deviceSpecificKeys.ToArray();
            AnyKeyboardKeys = anyKeyboardKeys.ToArray();
            return Task.CompletedTask;
        }

        public Task SetAdditionalButtonDeviceRoutingAsync(
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            AdditionalButtonDeviceRoutingEnabled = isEnabled;
            return Task.CompletedTask;
        }

        public Task SetDeviceIdentificationKeysAsync(
            IReadOnlyCollection<DeckKey> numLockOffKeys,
            IReadOnlyCollection<DeckKey> numLockOnKeys,
            CancellationToken cancellationToken = default)
        {
            DeviceIdentificationKeysOff = numLockOffKeys.ToArray();
            DeviceIdentificationKeysOn = numLockOnKeys.ToArray();
            return Task.CompletedTask;
        }

        public Task SetNumLockAsync(bool isOn, CancellationToken cancellationToken = default)
        {
            NumLockStateChanged?.Invoke(this, new NumLockStateChangedEventArgs(isOn));
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<CapturedKeyPress> ReadAllAsync(CancellationToken cancellationToken = default) =>
            channel.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync() => default;

        public void Publish(DeckKey key) => channel.Writer.TryWrite(
            new CapturedKeyPress(key, NumLockLayer.Off, DateTimeOffset.UtcNow));

        public void PublishObservedInput(
            uint virtualKeyCode,
            uint scanCode,
            bool isExtended,
            bool isKeyDown = true) =>
            KeyboardInputObserved?.Invoke(
                this,
                new KeyboardInputObservedEventArgs(virtualKeyCode, scanCode, isExtended, isKeyDown));

        public void PublishRawInput(
            uint virtualKeyCode,
            bool isSelectedDevice,
            bool isKeyDown = true,
            string? deviceId = null)
        {
            deviceId ??= isSelectedDevice
                ? SelectedKeyboardDeviceId ?? "test"
                : "test";
            var capturedKeyPress = isSelectedDevice
                && isKeyDown
                && virtualKeyCode == 0x60
                    ? new CapturedKeyPress(
                        DeckKey.Numpad0,
                        NumLockLayer.Off,
                        DateTimeOffset.UtcNow,
                        virtualKeyCode,
                        deviceId)
                    : (CapturedKeyPress?)null;
            RawKeyboardInputObserved?.Invoke(this,
                new RawKeyboardInputObservedEventArgs(
                deviceId,
                    virtualKeyCode,
                    0,
                    false,
                    isSelectedDevice,
                    isKeyDown,
                    capturedKeyPress));
            if (capturedKeyPress is CapturedKeyPress keyPress
                && !DeviceSpecificKeys.Contains(keyPress.Key))
            {
                channel.Writer.TryWrite(keyPress);
            }
        }

        public void PublishDeviceKey(
            DeckKey key,
            string deviceId,
            bool isSelectedDevice,
            bool isRepeat = false)
        {
            var keyPress = new CapturedKeyPress(
                key,
                NumLockLayer.Off,
                DateTimeOffset.UtcNow,
                DeviceId: deviceId);
            RawKeyboardInputObserved?.Invoke(
                this,
                new RawKeyboardInputObservedEventArgs(
                    deviceId,
                    0x60,
                    0,
                    false,
                    isSelectedDevice,
                    capturedKeyPress: keyPress,
                    isRepeat: isRepeat));

            if (isSelectedDevice
                && !DeviceSpecificKeys.Contains(key)
                && !AnyKeyboardKeys.Contains(key))
            {
                channel.Writer.TryWrite(keyPress);
            }
        }
    }

    private sealed class SequenceRecordingExecutor : IActionExecutor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> executedPaths = new();
        private int executionCount;

        public TaskCompletionSource<bool> FirstExecution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SecondExecution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ThirdExecution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<string> ExecutedPaths => executedPaths.ToArray();

        public bool CanExecute(ActionDefinition action) => action is OpenPathActionDefinition;

        public Task ExecuteAsync(ActionDefinition action, CancellationToken cancellationToken = default)
        {
            var path = ((OpenPathActionDefinition)action).Path;
            executedPaths.Enqueue(path);
            var execution = Interlocked.Increment(ref executionCount);
            if (execution == 1)
            {
                FirstExecution.TrySetResult(true);
            }
            else if (execution == 2)
            {
                SecondExecution.TrySetResult(true);
            }
            else
            {
                ThirdExecution.TrySetResult(true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryConfigurationStore(AppConfiguration configuration) : IConfigurationStore
    {
        public Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task SaveAsync(AppConfiguration updatedConfiguration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
