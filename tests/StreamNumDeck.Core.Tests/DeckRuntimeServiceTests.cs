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

        public event EventHandler<KeyboardCaptureStateChangedEventArgs>? StateChanged;

        public event EventHandler<NumLockStateChangedEventArgs>? NumLockStateChanged;

        public event EventHandler<KeyboardInputObservedEventArgs>? KeyboardInputObserved;

        public event EventHandler<RawKeyboardInputObservedEventArgs>? RawKeyboardInputObserved
        {
            add { }
            remove { }
        }

        public event EventHandler<KeyboardDeviceCorrelationObservedEventArgs>? KeyboardDeviceCorrelationObserved
        {
            add { }
            remove { }
        }

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

        public Task SetDeviceIdentificationKeysAsync(
            IReadOnlyCollection<DeckKey> numLockOffKeys,
            IReadOnlyCollection<DeckKey> numLockOnKeys,
            CancellationToken cancellationToken = default)
        {
            DeviceIdentificationKeysOff = numLockOffKeys.ToArray();
            DeviceIdentificationKeysOn = numLockOnKeys.ToArray();
            return Task.CompletedTask;
        }

        public Task SetKeyboardDiagnosticModeAsync(
            bool isEnabled,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        public void PublishObservedInput(uint virtualKeyCode, uint scanCode, bool isExtended) =>
            KeyboardInputObserved?.Invoke(
                this,
                new KeyboardInputObservedEventArgs(virtualKeyCode, scanCode, isExtended));
    }

    private sealed class InMemoryConfigurationStore(AppConfiguration configuration) : IConfigurationStore
    {
        public Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task SaveAsync(AppConfiguration updatedConfiguration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
