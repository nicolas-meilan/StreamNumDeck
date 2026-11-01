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
    public async Task Runtime_UpdatesKeyboardRemappingsWhenConfigurationChanges()
    {
        var assignment = KeyAssignment.Empty with { RemappedVirtualKeyCode = 0x7C };
        var configuration = AppConfiguration.CreateDefault();
        var profile = configuration.ActiveProfile
            .WithAssignment(NumLockLayer.Off, DeckKey.Numpad1, assignment)
            .WithAssignment(
                NumLockLayer.On,
                DeckKey.Numpad1,
                KeyAssignment.Empty with { RemappedVirtualKeyCode = 0x87 });
        configuration = configuration.ReplaceProfile(profile);
        using var configurationService = new ConfigurationService(
            new InMemoryConfigurationStore(configuration));
        var capture = new TestKeyboardCaptureService();
        var runtime = new DeckRuntimeService(
            configurationService,
            capture,
            new ActionDispatcher(Array.Empty<IActionExecutor>()));

        try
        {
            await runtime.StartAsync();
            Assert.AreEqual((uint)0x7C, capture.NumLockOffRemappings[DeckKey.Numpad1]);
            Assert.AreEqual((uint)0x87, capture.NumLockOnRemappings[DeckKey.Numpad1]);

            await configurationService.UpdateAsync(current =>
            {
                var activeProfile = current.ActiveProfile.WithAssignment(
                    NumLockLayer.Off,
                    DeckKey.Numpad1,
                    KeyAssignment.Empty with { RemappedVirtualKeyCode = 0x80 });
                return current.ReplaceProfile(activeProfile);
            });

            Assert.AreEqual((uint)0x80, capture.NumLockOffRemappings[DeckKey.Numpad1]);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RemappedPhysicalKey_RunsItsConfiguredAction()
    {
        var configuration = AppConfiguration.CreateDefault();
        var assignment = new KeyAssignment(
            "F24 action",
            IconReference.BuiltIn("play"),
            new OpenPathActionDefinition("f24-action"))
        {
            RemappedVirtualKeyCode = KeyAssignment.LastRemappableVirtualKeyCode,
        };
        var profile = configuration.ActiveProfile.WithAssignment(
            NumLockLayer.Off,
            DeckKey.NumpadEnter,
            assignment);
        configuration = configuration.ReplaceProfile(profile);
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
            await capture.SetSelectedKeyboardDeviceAsync("selected-keyboard");
            await runtime.StartAsync();
            Assert.AreEqual(
                KeyAssignment.LastRemappableVirtualKeyCode,
                capture.NumLockOffRemappings[DeckKey.NumpadEnter]);

            capture.PublishRemappedKey(DeckKey.NumpadEnter);

            await WaitWithTimeoutAsync(executor.FirstExecution.Task);
            CollectionAssert.AreEqual(new[] { "f24-action" }, executor.ExecutedPaths.ToArray());
        }
        finally
        {
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
    public async Task AdditionalButton_RespectsNumpadCaptureToggleAndRunsGloballyWhenEnabled()
    {
        var configuration = AppConfiguration.CreateDefault();
        var additional = new AdditionalButtonSettings(0, 0x41,
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
            capture.PublishObservedInput(0x41, 0x1E, isExtended: false);
            capture.PublishObservedInput(0x41, 0x1E, isExtended: false, isKeyDown: false);
            await Task.Delay(100);
            Assert.AreEqual(0, executor.StartCount, "An additional key must be ignored while NumPad capture is disabled.");

            await capture.SetCaptureTargetsAsync(KeyboardCaptureTargets.Numpad);
            capture.PublishObservedInput(0x41, 0x1E, isExtended: false);
            await WaitWithTimeoutAsync(executor.FirstStarted.Task);
        }
        finally
        {
            executor.ReleaseFirst.TrySetResult(true);
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
            Assert.IsTrue(executor.FirstExecution.Task.IsCompleted, "A non-ambiguous key must execute immediately.");
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
            [DeckKey.NumpadBack],
            [DeckKey.Insert]);

        CollectionAssert.AreEqual(new[] { DeckKey.NumpadBack }, capture.DeviceIdentificationKeysOff.ToArray());
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

        public IReadOnlyDictionary<DeckKey, uint> NumLockOffRemappings { get; private set; } =
            new Dictionary<DeckKey, uint>();

        public IReadOnlyDictionary<DeckKey, uint> NumLockOnRemappings { get; private set; } =
            new Dictionary<DeckKey, uint>();

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

        public void SetKeyRemappings(
            IReadOnlyDictionary<DeckKey, uint> numLockOffRemappings,
            IReadOnlyDictionary<DeckKey, uint> numLockOnRemappings)
        {
            NumLockOffRemappings = numLockOffRemappings.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            NumLockOnRemappings = numLockOnRemappings.ToDictionary(static pair => pair.Key, static pair => pair.Value);
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

        public void PublishRemappedKey(DeckKey key)
        {
            if (NumLockOffRemappings.ContainsKey(key))
            {
                channel.Writer.TryWrite(new CapturedKeyPress(key, NumLockLayer.Off, DateTimeOffset.UtcNow));
            }
        }

        public void PublishObservedInput(
            uint virtualKeyCode,
            uint scanCode,
            bool isExtended,
            bool isKeyDown = true) =>
            KeyboardInputObserved?.Invoke(
                this,
                new KeyboardInputObservedEventArgs(virtualKeyCode, scanCode, isExtended, isKeyDown));

        public void PublishRawInput(uint virtualKeyCode, bool isSelectedDevice, bool isKeyDown = true)
        {
            var capturedKeyPress = isSelectedDevice
                && isKeyDown
                && virtualKeyCode == 0x60
                    ? new CapturedKeyPress(
                        DeckKey.Numpad0,
                        NumLockLayer.Off,
                        DateTimeOffset.UtcNow,
                        virtualKeyCode)
                    : (CapturedKeyPress?)null;
            RawKeyboardInputObserved?.Invoke(this,
                new RawKeyboardInputObservedEventArgs(
                    "test",
                    virtualKeyCode,
                    0,
                    false,
                    isSelectedDevice,
                    isKeyDown,
                    capturedKeyPress));
            if (capturedKeyPress is CapturedKeyPress keyPress)
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
