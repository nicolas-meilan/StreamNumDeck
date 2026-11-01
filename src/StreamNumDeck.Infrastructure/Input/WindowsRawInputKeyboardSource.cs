using System.ComponentModel;
using System.Runtime.InteropServices;
using StreamNumDeck.Core.Input;

namespace StreamNumDeck.Infrastructure.Input;

internal sealed class WindowsRawInputKeyboardSource : IAsyncDisposable
{
    private const uint RidInput = 0x10000003;
    private const uint RidDeviceName = 0x20000007;
    private const uint RimTypeKeyboard = 1;
    private const uint WmInput = 0x00FF;
    private const uint WmInputDeviceChange = 0x00FE;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDeviceNotify = 0x00002000;
    private const uint RidevRemove = 0x00000001;
    private const uint RiKeyBreak = 0x0001;
    private const uint RiKeyE0 = 0x0002;
    private const uint PmNoRemove = 0;
    private static readonly nint HwndMessage = new IntPtr(-3);

    private readonly string? selectedDeviceId;
    private readonly Action<string, uint, uint, bool, bool> inputObserved;
    private readonly string windowClassName = $"StreamNumDeck.RawInput.{Guid.NewGuid():N}";
    private readonly WndProc windowProcedure;
    private readonly TaskCompletionSource<object?> startupCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<nint, string> devicePaths = [];
    private Thread? thread;
    private uint threadId;
    private nint windowHandle;
    private Exception? windowProcedureFailure;
    private bool rawInputRegistered;

    public WindowsRawInputKeyboardSource(
        string? selectedDeviceId,
        Action<string, uint, uint, bool, bool> inputObserved)
    {
        this.selectedDeviceId = selectedDeviceId;
        this.inputObserved = inputObserved;
        windowProcedure = WindowProc;
    }

    public event Action<Exception>? Faulted;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (thread is not null)
        {
            throw new InvalidOperationException("The Raw Input keyboard source is already running.");
        }

        thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "StreamNumDeck Raw Input keyboard",
        };
        thread.Start();
        return WaitForStartupAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var currentThread = thread;
        if (currentThread is null)
        {
            return;
        }

        while (currentThread.IsAlive && threadId == 0)
        {
            await Task.Yield();
        }

        if (currentThread.IsAlive && !PostThreadMessage(threadId, 0x0012, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to stop the Raw Input keyboard message loop.");
        }

        await Task.Run(currentThread.Join).ConfigureAwait(false);
        thread = null;
    }

    public static Task<IReadOnlyList<KeyboardDeviceInfo>> GetAvailableKeyboardsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<KeyboardDeviceInfo> devices = EnumerateKeyboardDevices()
            .OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return Task.FromResult(devices);
    }

    private async Task WaitForStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await startupCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var currentThread = thread;
            while (currentThread is { IsAlive: true } && threadId == 0)
            {
                await Task.Yield();
            }

            if (currentThread is { IsAlive: true } && threadId != 0)
            {
                PostThreadMessage(threadId, 0x0012, 0, 0);
                await Task.Run(currentThread.Join).ConfigureAwait(false);
            }

            thread = null;
            throw;
        }
    }

    private void ThreadMain()
    {
        try
        {
            threadId = GetCurrentThreadId();
            PeekMessage(out _, 0, 0, 0, PmNoRemove);

            var instance = GetModuleHandle(null);
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = windowProcedure,
                Instance = instance,
                ClassName = windowClassName,
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to register the Raw Input keyboard window.");
            }

            windowHandle = CreateWindowEx(
                0,
                windowClassName,
                windowClassName,
                0,
                0,
                0,
                0,
                0,
                HwndMessage,
                0,
                instance,
                0);
            if (windowHandle == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the Raw Input keyboard window.");
            }

            RefreshDevicePaths();
            if (selectedDeviceId is not null
                && !devicePaths.Values.Contains(selectedDeviceId, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected keyboard is not currently connected.");
            }

            var registration = new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x06,
                Flags = RidevInputSink | RidevDeviceNotify,
                Target = windowHandle,
            };
            if (!RegisterRawInputDevices([registration], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to register for Raw Input keyboard events.");
            }

            rawInputRegistered = true;
            startupCompletion.TrySetResult(null);
            while (true)
            {
                var result = GetMessage(out var message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The Raw Input keyboard message loop failed.");
                }

                TranslateMessage(in message);
                DispatchMessage(in message);
                if (windowProcedureFailure is not null)
                {
                    throw new InvalidOperationException("Raw Input keyboard processing failed.", windowProcedureFailure);
                }
            }
        }
        catch (Exception exception)
        {
            if (!startupCompletion.TrySetException(exception))
            {
                Faulted?.Invoke(exception);
            }
        }
        finally
        {
            if (rawInputRegistered)
            {
                var registration = new RawInputDevice
                {
                    UsagePage = 0x01,
                    Usage = 0x06,
                    Flags = RidevRemove,
                };
                if (!RegisterRawInputDevices([registration], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                {
                    Faulted?.Invoke(new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to unregister Raw Input keyboard events."));
                }

                rawInputRegistered = false;
            }

            if (windowHandle != 0)
            {
                DestroyWindow(windowHandle);
                windowHandle = 0;
            }

            UnregisterClass(windowClassName, GetModuleHandle(null));
            threadId = 0;
        }
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == WmInput)
            {
                ProcessInput(lParam);
            }
            else if (message == WmInputDeviceChange)
            {
                RefreshDevicePaths();
            }
        }
        catch (Exception exception)
        {
            windowProcedureFailure = exception;
            PostQuitMessage(1);
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ProcessInput(nint rawInputHandle)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        var result = GetRawInputData(rawInputHandle, RidInput, 0, ref size, headerSize);
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read a Raw Input keyboard event.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            result = GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize);
            if (result == uint.MaxValue || result != size)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read a Raw Input keyboard event.");
            }

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RimTypeKeyboard
                || !devicePaths.TryGetValue(header.Device, out var devicePath))
            {
                return;
            }

            var keyboard = Marshal.PtrToStructure<RawKeyboard>(
                IntPtr.Add(buffer, checked((int)headerSize)));
            var isKeyDown = (keyboard.Flags & RiKeyBreak) == 0;
            inputObserved(
                devicePath,
                keyboard.VirtualKey,
                keyboard.MakeCode,
                (keyboard.Flags & RiKeyE0) != 0,
                isKeyDown);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void RefreshDevicePaths()
    {
        devicePaths.Clear();
        foreach (var device in EnumerateRawKeyboardDevices())
        {
            devicePaths[device.Handle] = device.DevicePath;
        }
    }

    private static IReadOnlyList<RawKeyboardDevice> EnumerateRawKeyboardDevices()
    {
        uint deviceCount = 0;
        var result = GetRawInputDeviceList(0, ref deviceCount, (uint)Marshal.SizeOf<RawInputDeviceListEntry>());
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enumerate keyboard devices.");
        }

        if (deviceCount == 0)
        {
            return [];
        }

        var entrySize = Marshal.SizeOf<RawInputDeviceListEntry>();
        var buffer = Marshal.AllocHGlobal(checked((int)deviceCount * entrySize));
        try
        {
            result = GetRawInputDeviceList(
                buffer,
                ref deviceCount,
                (uint)entrySize);
            if (result == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enumerate keyboard devices.");
            }

            var devices = new List<RawKeyboardDevice>();
            for (var index = 0; index < result; index++)
            {
                var entry = Marshal.PtrToStructure<RawInputDeviceListEntry>(
                    IntPtr.Add(buffer, checked((int)index * entrySize)));
                if (entry.Type != RimTypeKeyboard)
                {
                    continue;
                }

                var nameLength = 0u;
                var nameResult = GetRawInputDeviceInfo(entry.Device, RidDeviceName, 0, ref nameLength);
                if (nameResult == uint.MaxValue || nameLength == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read a keyboard device name.");
                }

                var nameBuffer = Marshal.AllocHGlobal(checked((int)nameLength * sizeof(char)));
                try
                {
                    nameResult = GetRawInputDeviceInfo(entry.Device, RidDeviceName, nameBuffer, ref nameLength);
                    if (nameResult == uint.MaxValue)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read a keyboard device name.");
                    }

                    var devicePath = Marshal.PtrToStringUni(nameBuffer);
                    if (!string.IsNullOrWhiteSpace(devicePath))
                    {
                        devices.Add(new RawKeyboardDevice(entry.Device, devicePath));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(nameBuffer);
                }
            }

            return devices;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<KeyboardDeviceInfo> EnumerateKeyboardDevices()
    {
        var devices = new Dictionary<string, KeyboardDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in EnumerateRawKeyboardDevices())
        {
            devices[device.DevicePath] = new KeyboardDeviceInfo(
                device.DevicePath,
                CreateDisplayName(device.DevicePath));
        }

        return devices.Values.ToArray();
    }

    private static string CreateDisplayName(string devicePath)
    {
        var parts = devicePath.Split(new[] { '#' }, StringSplitOptions.RemoveEmptyEntries);
        var hardwareId = parts.FirstOrDefault(part =>
            part.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0
            && part.IndexOf("PID_", StringComparison.OrdinalIgnoreCase) >= 0);
        var instanceId = parts.Length > 2 ? parts[2] : devicePath;
        var shortInstanceId = instanceId.Length > 30
            ? $"{instanceId.Substring(0, 18)}...{instanceId.Substring(instanceId.Length - 8)}"
            : instanceId;
        return hardwareId is null
            ? $"Keyboard · {shortInstanceId}"
            : $"{hardwareId} · {shortInstanceId}";
    }

    private sealed record RawKeyboardDevice(nint Handle, string DevicePath);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public WndProc WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceListEntry
    {
        public nint Device;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nuint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceList", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        nint devices,
        ref uint deviceCount,
        uint deviceSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        nint device,
        uint command,
        nint data,
        ref uint size);

    [DllImport("user32.dll", EntryPoint = "RegisterRawInputDevices", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint count,
        uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputData", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint rawInput,
        uint command,
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out Message message, nint window, uint minimumMessage, uint maximumMessage);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in Message message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(in Message message);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint minimumMessage, uint maximumMessage, uint removeMessage);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostQuitMessage")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
