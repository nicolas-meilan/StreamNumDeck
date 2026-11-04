using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StreamNumDeck.Core.Input;
using StreamNumDeck.Core.Settings;
using StreamNumDeck.Wpf.Services;
using StreamNumDeck.Wpf.ViewModels;
using StreamNumDeck.Wpf.Localization;
using StreamNumDeck.Wpf.Presentation;

namespace StreamNumDeck.Wpf;

public partial class SettingsWindow : Window
{
    private static readonly Uri SupportUri = new(
        "https://www.paypal.com/donate/?hosted_button_id=83U8QE9H5UFMS");
    private static readonly Uri BaseProjectUri = new("https://github.com/nikartom/StreamNumDeck");
    private static readonly Uri LatestReleaseUri = new("https://github.com/nicolas-meilan/StreamNumDeck/releases/latest");
    private static readonly Uri LatestReleaseApiUri = new(
        "https://api.github.com/repos/nicolas-meilan/StreamNumDeck/releases/latest");
    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();

    private readonly MainViewModel viewModel;
    private readonly bool focusVTubeStudio;
    private GlobalSettings? originalSettings;
    private volatile bool keyboardDiagnosticActive;
    private IReadOnlyList<KeyboardDeviceInfo> availableKeyboards = [];

    public SettingsWindow(MainViewModel viewModel, bool focusVTubeStudio = false)
    {
        this.viewModel = viewModel;
        this.focusVTubeStudio = focusVTubeStudio;
        InitializeComponent();
        this.viewModel.RawKeyboardInputObserved += ViewModel_RawKeyboardInputObserved;
        Closed += Window_Closed;
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StreamNumDeck", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = await viewModel.LoadSettingsAsync();
            originalSettings = snapshot.Settings;
            AudioDeviceComboBox.ItemsSource = snapshot.AudioDevices;
            AudioDeviceComboBox.SelectedValue = snapshot.Settings.AudioOutputDeviceId;
            if (AudioDeviceComboBox.SelectedIndex < 0)
            {
                AudioDeviceComboBox.SelectedIndex = 0;
            }

            MasterVolumeSlider.Value = snapshot.Settings.MasterVolume;
            ConcurrentSoundsCheckBox.IsChecked = snapshot.Settings.AllowConcurrentSounds;
            PreloadSoundsCheckBox.IsChecked = snapshot.Settings.PreloadShortSounds;
            StartWithWindowsCheckBox.IsChecked = snapshot.Settings.StartWithWindows;
            MinimizeToTrayCheckBox.IsChecked = snapshot.Settings.MinimizeToTray;
            CaptureOnStartupCheckBox.IsChecked = snapshot.Settings.EnableCaptureOnStartup;
            ShowMuteOverlayCheckBox.IsChecked = snapshot.Settings.ShowMuteOverlay;
            CaptureNumpadCheckBox.IsChecked = snapshot.Settings.CaptureNumpad;
            CaptureNavigationBlockCheckBox.IsChecked = snapshot.Settings.CaptureNavigationBlock;
            NumLockBehaviorComboBox.SelectedIndex = snapshot.Settings.NumLockBehavior is NumLockBehavior.ActionKey ? 1 : 0;
            await RefreshKeyboardDeviceListAsync(snapshot.Settings.SelectedKeyboardDeviceId);
            ObsHostTextBox.Text = snapshot.Settings.Obs.Host;
            ObsPortTextBox.Text = snapshot.Settings.Obs.Port.ToString();
            VTubeStudioHostTextBox.Text = snapshot.Settings.VTubeStudio.Host;
            VTubeStudioPortTextBox.Text = snapshot.Settings.VTubeStudio.Port.ToString();
            ObsPasswordBox.Password = string.Empty;
            PasswordStoredText.Visibility = string.IsNullOrEmpty(snapshot.ObsPassword)
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (focusVTubeStudio)
            {
                VTubeStudioSetupNotice.Visibility = Visibility.Visible;
                UpdateLayout();
                VTubeStudioCard.BringIntoView();
                VTubeStudioHostTextBox.Focus();
            }

            await CheckForUpdatesAsync();
        }
        catch (Exception exception)
        {
            ShowErrorStatus("Error_LoadSettings", "Could not load settings", exception);
            SaveButton.IsEnabled = false;
        }
    }

    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MasterVolumeText is not null)
        {
            MasterVolumeText.Text = $"{Math.Round(e.NewValue):0}%";
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (originalSettings is null)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            if (!int.TryParse(ObsPortTextBox.Text, out var port) || port is < 1 or > 65535)
            {
                throw new FormatException($"{AppStrings.Get("Settings_ObsPort.Header", "Port")}: 1–65535");
            }

            if (!int.TryParse(VTubeStudioPortTextBox.Text, out var vTubeStudioPort)
                || vTubeStudioPort is < 1 or > 65535)
            {
                throw new FormatException($"{AppStrings.Get("Settings_VTubeStudioPort.Header", "Port")}: 1–65535");
            }

            var selectedKeyboardDeviceId = KeyboardDeviceComboBox.SelectedValue as string;
            if (selectedKeyboardDeviceId is { Length: > 0 }
                && !availableKeyboards.Any(device => string.Equals(
                    device.DeviceId,
                    selectedKeyboardDeviceId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new FormatException(AppStrings.Get(
                    "KeyboardDevice_Disconnected.Error",
                    "Reconnect the selected keyboard or choose All keyboards before saving."));
            }

            var settings = new GlobalSettings(
                AudioDeviceComboBox.SelectedValue as string,
                checked((int)Math.Round(MasterVolumeSlider.Value)),
                ConcurrentSoundsCheckBox.IsChecked == true,
                PreloadSoundsCheckBox.IsChecked == true,
                StartWithWindowsCheckBox.IsChecked == true,
                MinimizeToTrayCheckBox.IsChecked == true,
                CaptureOnStartupCheckBox.IsChecked == true,
                originalSettings.Theme,
                new ObsConnectionSettings(ObsHostTextBox.Text, port, originalSettings.Obs.CredentialKey))
            {
                CaptureNumpad = CaptureNumpadCheckBox.IsChecked == true,
                CaptureNavigationBlock = CaptureNavigationBlockCheckBox.IsChecked == true,
                NumLockBehavior = NumLockBehaviorComboBox.SelectedIndex == 1
                    ? NumLockBehavior.ActionKey
                    : NumLockBehavior.SwitchLayers,
                SelectedKeyboardDeviceId = selectedKeyboardDeviceId ?? string.Empty,
                ShowMuteOverlay = ShowMuteOverlayCheckBox.IsChecked == true,
                VTubeStudio = new VTubeStudioConnectionSettings(
                    VTubeStudioHostTextBox.Text,
                    vTubeStudioPort,
                    originalSettings.VTubeStudio.CredentialKey),
            };

            var previousStartupState = WindowsStartupService.IsEnabled();
            WindowsStartupService.SetEnabled(settings.StartWithWindows);

            try
            {
                await StopKeyboardDiagnosticAsync();
                await viewModel.SaveSettingsAsync(settings, ObsPasswordBox.Password);
            }
            catch
            {
                try
                {
                    WindowsStartupService.SetEnabled(previousStartupState);
                }
                catch (Exception rollbackException)
                {
                    AppLogger.Error("Restore Windows startup setting", rollbackException);
                }

                throw;
            }

            await ((App)System.Windows.Application.Current).ApplySettingsAsync(settings);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ShowErrorStatus("Error_SaveSettings", "Could not save settings", exception);
            SaveButton.IsEnabled = true;
        }
    }

    private async void RefreshKeyboardDevices_Click(object sender, RoutedEventArgs e) =>
        await RefreshKeyboardDeviceListAsync(KeyboardDeviceComboBox.SelectedValue as string);

    private async Task RefreshKeyboardDeviceListAsync(string? selectedDeviceId)
    {
        availableKeyboards = [];
        try
        {
            availableKeyboards = await viewModel.GetAvailableKeyboardsAsync();
            var options = new List<KeyboardDeviceOption>
            {
                new(
                    string.Empty,
                    AppStrings.Get("KeyboardDevice_All.Label", "All keyboards (global capture)")),
            };
            options.AddRange(availableKeyboards.Select(device =>
                new KeyboardDeviceOption(device.DeviceId, device.DisplayName)));

            if (selectedDeviceId is { Length: > 0 } disconnectedDeviceId
                && !availableKeyboards.Any(device => string.Equals(
                    device.DeviceId,
                    disconnectedDeviceId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new KeyboardDeviceOption(
                    disconnectedDeviceId,
                    AppStrings.Get("KeyboardDevice_Disconnected.Simple", "Disconnected keyboard")));
            }

            KeyboardDeviceComboBox.ItemsSource = options;
            KeyboardDeviceComboBox.SelectedValue = selectedDeviceId ?? string.Empty;
        }
        catch (Exception exception)
        {
            ShowErrorStatus(
                "KeyboardDevice_EnumerationFailed",
                "Could not enumerate keyboard devices",
                exception);
        }
    }

    private async void StartKeyboardDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        StartKeyboardDiagnosticButton.IsEnabled = false;
        try
        {
            KeyboardScanCodeDiagnosticOutput.Clear();
            await viewModel.StartKeyboardDiagnosticAsync();
            keyboardDiagnosticActive = true;
            StopKeyboardDiagnosticButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            StartKeyboardDiagnosticButton.IsEnabled = true;
            ShowErrorStatus("Error_StartKeyboardDiagnostic", "Could not start keyboard diagnostic", exception);
        }
    }

    private async void StopKeyboardDiagnostic_Click(object sender, RoutedEventArgs e) =>
        await StopKeyboardDiagnosticAsync();

    private async Task StopKeyboardDiagnosticAsync()
    {
        if (!keyboardDiagnosticActive)
        {
            return;
        }

        keyboardDiagnosticActive = false;
        try
        {
            await viewModel.StopKeyboardDiagnosticAsync();
        }
        finally
        {
            StartKeyboardDiagnosticButton.IsEnabled = true;
            StopKeyboardDiagnosticButton.IsEnabled = false;
        }
    }

    private void ViewModel_RawKeyboardInputObserved(object? sender, RawKeyboardInputObservedEventArgs e)
    {
        if (!e.IsKeyDown || !keyboardDiagnosticActive || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!keyboardDiagnosticActive)
            {
                return;
            }

            var extended = AppStrings.Get(
                e.IsExtended ? "Settings_ScanCodeExtended.Yes" : "Settings_ScanCodeExtended.No",
                e.IsExtended ? "yes" : "no");
            var entry = AppStrings.Format(
                "Settings_RawInputDiagnosticSimpleEntry",
                e.VirtualKeyCode,
                e.ScanCode,
                extended,
                AdditionalButtonKeyPresentation.GetLabel(e.VirtualKeyCode),
                GetShortKeyboardDeviceId(e.DeviceId));
            AppendKeyboardDiagnosticEntry(entry);
        }));
    }

    private static string GetShortKeyboardDeviceId(string deviceId)
    {
        var interfaceSeparator = deviceId.LastIndexOf("#{", StringComparison.Ordinal);
        var deviceInstancePath = interfaceSeparator >= 0
            ? deviceId.Substring(0, interfaceSeparator)
            : deviceId;
        var instanceSeparator = deviceInstancePath.LastIndexOf('#');
        var instanceId = instanceSeparator >= 0
            ? deviceInstancePath.Substring(instanceSeparator + 1)
            : deviceInstancePath;
        return instanceId.Length > 8 ? $"{instanceId.Substring(0, 8)}..." : instanceId;
    }

    private void AppendKeyboardDiagnosticEntry(string entry)
    {
        var lines = KeyboardScanCodeDiagnosticOutput.Text
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
        var firstLine = Math.Max(0, lines.Length - 7);
        KeyboardScanCodeDiagnosticOutput.Text = string.Join(
            Environment.NewLine,
            lines.Skip(firstLine).Append(entry));
        KeyboardScanCodeDiagnosticOutput.ScrollToEnd();
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        viewModel.RawKeyboardInputObserved -= ViewModel_RawKeyboardInputObserved;
        try
        {
            await StopKeyboardDiagnosticAsync();
        }

        catch (Exception exception)
        {
            AppLogger.Error("Stop keyboard diagnostic", exception);
        }
    }

    private sealed record KeyboardDeviceOption(string DeviceId, string DisplayName);

    private async void TestObs_Click(object sender, RoutedEventArgs e)
    {
        TestObsButton.IsEnabled = false;
        ObsTestResultText.Visibility = Visibility.Collapsed;

        try
        {
            if (!int.TryParse(ObsPortTextBox.Text, out var port) || port is < 1 or > 65535)
            {
                throw new FormatException($"{AppStrings.Get("Settings_ObsPort.Header", "Port")}: 1–65535");
            }

            ObsTestResultText.Text = await viewModel.TestObsConnectionAsync(
                ObsHostTextBox.Text,
                port,
                ObsPasswordBox.Password);
            ObsTestResultText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            ShowErrorStatus("Obs_ConnectionFailed", "Could not connect to OBS", exception);
        }
        finally
        {
            TestObsButton.IsEnabled = true;
        }
    }

    private async void TestVTubeStudio_Click(object sender, RoutedEventArgs e)
    {
        TestVTubeStudioButton.IsEnabled = false;
        VTubeStudioTestResultText.Text = AppStrings.Get(
            "VTubeStudio_WaitingForApproval",
            "Waiting for approval in VTube Studio…");
        VTubeStudioTestResultText.Foreground = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFD166"));
        VTubeStudioTestResultText.Visibility = Visibility.Visible;

        try
        {
            if (!int.TryParse(VTubeStudioPortTextBox.Text, out var port) || port is < 1 or > 65535)
            {
                throw new FormatException($"{AppStrings.Get("Settings_VTubeStudioPort.Header", "Port")}: 1–65535");
            }

            VTubeStudioTestResultText.Text = await viewModel.TestVTubeStudioConnectionAsync(
                VTubeStudioHostTextBox.Text,
                port);
            VTubeStudioTestResultText.Foreground = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#9DDB9F"));
            VTubeStudioTestResultText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            VTubeStudioTestResultText.Text = AppStrings.Get(
                "VTubeStudio_ConnectionFailed",
                "Could not connect to VTube Studio");
            VTubeStudioTestResultText.Foreground = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF99A4"));
            ShowErrorStatus(
                "VTubeStudio_ConnectionFailed",
                "Could not connect to VTube Studio",
                exception);
        }
        finally
        {
            TestVTubeStudioButton.IsEnabled = true;
        }
    }

    private async void PositionOverlay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await viewModel.PreviewMicrophoneOverlayAsync();
            ShowStatus(AppStrings.Get(
                "Settings_OverlayHint.Text",
                "Drag the indicator to the required monitor. Its position will be saved."), isError: false);
        }
        catch (Exception exception)
        {
            ShowErrorStatus("Error_ShowOverlay", "Could not show the microphone indicator", exception);
        }
    }

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUri(
            SupportUri,
            "Error_OpenSupportLink",
            "Could not open the support page");
    }

    private void BaseProject_Click(object sender, RoutedEventArgs e) =>
        OpenExternalUri(
            BaseProjectUri,
            "Error_OpenBaseProjectLink",
            "Could not open the base project page");

    private void Releases_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUri(
            LatestReleaseUri,
            "Error_OpenReleasesLink",
            "Could not open the releases page");
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateStatusText.Text = AppStrings.Get(
            "Settings_UpdateChecking.Text",
            "Checking for updates...");
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 184, 184));

        try
        {
            using var response = await UpdateHttpClient.GetAsync(LatestReleaseApiUri);
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!release.RootElement.TryGetProperty("tag_name", out var tagNameElement)
                || tagNameElement.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                throw new InvalidDataException("The latest release response did not contain a valid version tag.");
            }

            var latestVersion = ParseReleaseVersion(tagNameElement.GetString());
            if (latestVersion is null)
            {
                throw new InvalidDataException("The latest release response did not contain a valid version tag.");
            }

            var currentVersion = typeof(SettingsWindow).Assembly.GetName().Version
                ?? throw new InvalidDataException("The application assembly version is not available.");
            if (latestVersion > currentVersion)
            {
                var latestVersionText = latestVersion.ToString();
                UpdateBadgeText.Text = AppStrings.Format("Settings_UpdateBadge.Text", latestVersionText);
                UpdateBadge.Visibility = Visibility.Visible;
                UpdateStatusText.Text = AppStrings.Format("Settings_UpdateAvailable.Text", latestVersionText);
                UpdateStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 135, 135));
                LatestReleaseButton.Style = (Style)FindResource("UpdateAvailableButtonStyle");
            }
            else
            {
                UpdateStatusText.Text = AppStrings.Get(
                    "Settings_NoUpdate.Text",
                    "No newer version is available on the base project.");
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or InvalidDataException)
        {
            AppLogger.Error("Check for updates", exception);
            UpdateStatusText.Text = AppStrings.Get(
                "Settings_UpdateCheckFailed.Text",
                "Could not check for updates. Try again later.");
            UpdateStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 95));
        }
    }

    private static Version? ParseReleaseVersion(string? tagName)
    {
        if (tagName is null)
        {
            return null;
        }

        var numericVersion = tagName.Trim();
        if (numericVersion.Length == 0)
        {
            return null;
        }

        if (numericVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            numericVersion = numericVersion.Substring(1);
        }

        var prereleaseSeparator = numericVersion.IndexOf('-');
        if (prereleaseSeparator >= 0)
        {
            numericVersion = numericVersion.Substring(0, prereleaseSeparator);
        }

        if (!Version.TryParse(numericVersion, out var version) || version is null)
        {
            return null;
        }

        return version;
    }

    private void OpenExternalUri(Uri uri, string errorResourceKey, string errorFallback)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            ShowErrorStatus(errorResourceKey, errorFallback, exception);
        }
    }

    private void ShowErrorStatus(string resourceKey, string fallback, Exception exception) =>
        ShowStatus(
            UserErrorFormatter.Format(AppStrings.Get(resourceKey, fallback), exception),
            isError: true);

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusIcon.Text = isError ? "\uEA39" : "\uE73E";
        StatusIcon.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            isError ? "#FF99A4" : "#9DDB9F"));
        StatusBorder.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            isError ? "#3A2022" : "#243425"));
        StatusBorder.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            isError ? "#D13438" : "#5B8F5D"));
        StatusBorder.Visibility = Visibility.Visible;
    }
}
