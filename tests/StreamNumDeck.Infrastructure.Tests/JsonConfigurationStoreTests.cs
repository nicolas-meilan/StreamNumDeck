using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamNumDeck.Core.Actions;
using StreamNumDeck.Core.Configuration;
using StreamNumDeck.Core.Deck;
using StreamNumDeck.Core.Icons;
using StreamNumDeck.Core.Settings;
using StreamNumDeck.Infrastructure.Configuration;

namespace StreamNumDeck.Infrastructure.Tests;

[TestClass]
public sealed class JsonConfigurationStoreTests
{
    private string testDirectory = null!;
    private ConfigurationPaths paths = null!;

    [TestInitialize]
    public void Initialize()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StreamNumDeck.Tests",
            Guid.NewGuid().ToString("N"));
        paths = new ConfigurationPaths(testDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WhenNoFileExists_CreatesValidDefaultConfiguration()
    {
        using var store = new JsonConfigurationStore(paths);

        var configuration = await store.LoadAsync();

        Assert.IsTrue(File.Exists(paths.ConfigurationFilePath));
        Assert.AreEqual("Основной стрим", configuration.ActiveProfile.Name);
        Assert.HasCount(23, configuration.ActiveProfile.NumLockOff.Assignments);
        Assert.HasCount(23, configuration.ActiveProfile.NumLockOn.Assignments);
        Assert.IsTrue(configuration.Settings.ShowMuteOverlay);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesMuteOverlaySetting()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault().WithSettings(
            GlobalSettings.Default with { ShowMuteOverlay = false });

        await store.SaveAsync(configuration);
        var loaded = await store.LoadAsync();

        Assert.IsFalse(loaded.Settings.ShowMuteOverlay);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesAdditionalButtonKeyAndAssignmentPerProfile()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault();
        var assignment = new KeyAssignment("Launch", IconReference.BuiltIn("star"), new StreamNumDeck.Core.Actions.OpenUriActionDefinition("https://example.com"))
        {
            KeyboardDeviceId = "keyboard-device-id",
            UseAnyKeyboard = true,
        };
        var profile = configuration.ActiveProfile.WithAdditionalButton(
            new AdditionalButtonSettings(3, 0x41, assignment));
        await store.SaveAsync(configuration.ReplaceProfile(profile));

        var loaded = await store.LoadAsync();
        var button = loaded.ActiveProfile.AdditionalButtons[3];
        Assert.AreEqual((uint)0x41, button.VirtualKeyCode);
        Assert.AreEqual("Launch", button.Assignment.Label);
        Assert.AreEqual("keyboard-device-id", button.Assignment.KeyboardDeviceId);
        Assert.IsTrue(button.Assignment.UseAnyKeyboard);
        Assert.IsInstanceOfType<StreamNumDeck.Core.Actions.OpenUriActionDefinition>(button.Assignment.Action);
        Assert.HasCount(8, loaded.ActiveProfile.AdditionalButtons);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesAdditionalButtonKeySequence()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault();
        var button = new AdditionalButtonSettings(2, null, KeyAssignment.Empty)
        {
            VirtualKeySequence = [0x60, 0x60],
        };
        await store.SaveAsync(configuration.ReplaceProfile(configuration.ActiveProfile.WithAdditionalButton(button)));

        var loaded = await store.LoadAsync();

        CollectionAssert.AreEqual(
            new uint[] { 0x60, 0x60 },
            loaded.ActiveProfile.AdditionalButtons[2].GetKeySequence().ToArray());
    }

    [TestMethod]
    public async Task LoadAsync_AcceptsAdditionalButtonsWithoutNewerProperties()
    {
        using var store = new JsonConfigurationStore(paths);
        await store.SaveAsync(AppConfiguration.CreateDefault());
        var root = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        foreach (var profile in root["configuration"]!["profiles"]!.AsArray())
        {
            foreach (var button in profile!["additionalButtons"]!.AsArray())
            {
                button!.AsObject().Remove("virtualKeySequence");
                button["assignment"]!.AsObject().Remove("keyboardDeviceId");
                button["assignment"]!.AsObject().Remove("useAnyKeyboard");
            }
        }

        File.WriteAllText(paths.ConfigurationFilePath, root.ToJsonString());

        var loaded = await store.LoadAsync();

        Assert.IsNull(loaded.ActiveProfile.AdditionalButtons[0].Assignment.KeyboardDeviceId);
        await store.SaveAsync(loaded);
        Assert.IsTrue(File.Exists(paths.ConfigurationFilePath));
    }

    [TestMethod]
    public async Task LoadAsync_MigratesAdditionalButtonKeyboardDeviceFromLegacyLocation()
    {
        using var store = new JsonConfigurationStore(paths);
        await store.SaveAsync(AppConfiguration.CreateDefault());
        var root = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        var additionalButton = root["configuration"]!["profiles"]![0]!["additionalButtons"]![0]!.AsObject();
        additionalButton["keyboardDeviceId"] = "legacy-keyboard";
        File.WriteAllText(paths.ConfigurationFilePath, root.ToJsonString());

        var loaded = await store.LoadAsync();

        Assert.AreEqual(
            "legacy-keyboard",
            loaded.ActiveProfile.AdditionalButtons[0].Assignment.KeyboardDeviceId);
        var migratedRoot = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        var migratedButton = migratedRoot["configuration"]!["profiles"]![0]!["additionalButtons"]![0]!;
        Assert.IsNull(migratedButton["keyboardDeviceId"]);
        Assert.AreEqual(
            "legacy-keyboard",
            migratedButton["assignment"]!["keyboardDeviceId"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task LoadAsync_WhenNoFileExists_UsesConfiguredDefaultProfileName()
    {
        using var store = new JsonConfigurationStore(
            paths,
            () => AppConfiguration.CreateDefault("Main profile"));

        var configuration = await store.LoadAsync();

        Assert.AreEqual("Main profile", configuration.ActiveProfile.Name);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesTypedActionAndIcon()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault();
        var profileWithIcon = new DeckProfile(
            configuration.ActiveProfile.Id,
            configuration.ActiveProfile.Name,
            configuration.ActiveProfile.NumLockOff,
            configuration.ActiveProfile.NumLockOn,
            IconReference.BuiltIn("star"));
        configuration = configuration.ReplaceProfile(profileWithIcon);
        var assignment = new KeyAssignment(
            "Аплодисменты",
            IconReference.CustomAsset("icons/applause.png"),
            new PlaySoundActionDefinition("C:\\Sounds\\applause.wav", 73, SoundPlaybackBehavior.StopOthers))
        {
            IdentifySelectedKeyboardBeforeBlocking = true,
            KeyboardDeviceId = @"\\?\HID#Keyboard#Specific",
        };
        var updatedProfile = configuration.ActiveProfile.WithAssignment(
            NumLockLayer.On,
            DeckKey.NumpadAdd,
            assignment);
        updatedProfile = updatedProfile.WithAssignment(
            NumLockLayer.On,
            DeckKey.NumpadMultiply,
            new KeyAssignment(
                "Any keyboard",
                IconReference.BuiltIn("play"),
                new OpenPathActionDefinition("any"))
            {
                UseAnyKeyboard = true,
            });
        var updatedConfiguration = configuration.ReplaceProfile(updatedProfile);

        await store.SaveAsync(updatedConfiguration);
        var loaded = await store.LoadAsync();

        var loadedAssignment = loaded.ActiveProfile.NumLockOn.GetAssignment(DeckKey.NumpadAdd);
        Assert.AreEqual("Аплодисменты", loadedAssignment.Label);
        Assert.AreEqual(IconKind.CustomAsset, loadedAssignment.Icon.Kind);
        Assert.AreEqual(IconReference.BuiltIn("star"), loaded.ActiveProfile.Icon);
        var soundAction = Assert.IsInstanceOfType<PlaySoundActionDefinition>(loadedAssignment.Action);
        Assert.AreEqual(73, soundAction.Volume);
        Assert.AreEqual(SoundPlaybackBehavior.StopOthers, soundAction.PlaybackBehavior);
        Assert.IsTrue(loadedAssignment.IdentifySelectedKeyboardBeforeBlocking);
        Assert.AreEqual(@"\\?\HID#Keyboard#Specific", loadedAssignment.KeyboardDeviceId);
        var anyKeyboardAssignment = loaded.ActiveProfile.NumLockOn.GetAssignment(DeckKey.NumpadMultiply);
        Assert.IsTrue(anyKeyboardAssignment.UseAnyKeyboard);
        Assert.IsNull(anyKeyboardAssignment.KeyboardDeviceId);
    }

    [TestMethod]
    public async Task LoadAsync_MigratesPerAssignmentDeviceBlockingOption()
    {
        using var store = new JsonConfigurationStore(paths);
        await store.SaveAsync(AppConfiguration.CreateDefault());
        var root = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        var assignment = root["configuration"]!["profiles"]![0]!["numLockOff"]!["assignments"]!["numpad1"]!;
        assignment.AsObject().Remove("identifySelectedKeyboardBeforeBlocking");
        assignment["experimentalDeviceSpecificBlocking"] = true;
        File.WriteAllText(paths.ConfigurationFilePath, root.ToJsonString());

        var loaded = await store.LoadAsync();

        Assert.IsTrue(loaded.ActiveProfile.NumLockOff.GetAssignment(DeckKey.Numpad1)
            .IdentifySelectedKeyboardBeforeBlocking);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesEmojiIcon()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault();
        var assignment = new KeyAssignment(
            "Праздник",
            IconReference.Emoji("🎉"),
            new OpenUriActionDefinition("https://example.com"));
        var updatedProfile = configuration.ActiveProfile.WithAssignment(
            NumLockLayer.On,
            DeckKey.NumpadAdd,
            assignment);

        await store.SaveAsync(configuration.ReplaceProfile(updatedProfile));
        var loaded = await store.LoadAsync();

        var loadedIcon = loaded.ActiveProfile.NumLockOn.GetAssignment(DeckKey.NumpadAdd).Icon;
        Assert.AreEqual(IconKind.Emoji, loadedIcon.Kind);
        Assert.AreEqual("🎉", loadedIcon.Value);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesKeyboardMacroSteps()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault();
        var macro = new KeyboardMacroActionDefinition(
            ImmutableArray.Create(
                KeyboardMacroStep.KeyPress(MacroKey.S, MacroModifiers.Control | MacroModifiers.Shift),
                KeyboardMacroStep.Delay(175),
                KeyboardMacroStep.KeyPress(MacroKey.Enter)));
        var profile = configuration.ActiveProfile.WithAssignment(
            NumLockLayer.Off,
            DeckKey.Numpad1,
            new KeyAssignment("Макрос", IconReference.BuiltIn("play"), macro));

        await store.SaveAsync(configuration.ReplaceProfile(profile));
        var loaded = await store.LoadAsync();

        var loadedMacro = Assert.IsInstanceOfType<KeyboardMacroActionDefinition>(
            loaded.ActiveProfile.NumLockOff.GetAssignment(DeckKey.Numpad1).Action);
        Assert.HasCount(3, loadedMacro.Steps);
        Assert.AreEqual(175, loadedMacro.Steps[1].DelayMilliseconds);
        Assert.AreEqual(MacroKey.Enter, loadedMacro.Steps[2].Chord!.Key);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesNewObsActions()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault();
        var profile = configuration.ActiveProfile.WithAssignment(
            NumLockLayer.Off,
            DeckKey.Numpad2,
            new KeyAssignment(
                "Медиа",
                IconReference.BuiltIn("play"),
                new ObsActionDefinition(ObsActionKind.ToggleMediaPlayPause, "Заставка")));

        await store.SaveAsync(configuration.ReplaceProfile(profile));
        var loaded = await store.LoadAsync();

        var action = Assert.IsInstanceOfType<ObsActionDefinition>(
            loaded.ActiveProfile.NumLockOff.GetAssignment(DeckKey.Numpad2).Action);
        Assert.AreEqual(ObsActionKind.ToggleMediaPlayPause, action.Action);
        Assert.AreEqual("Заставка", action.TargetName);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesVTubeStudioActionAndSettings()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault().WithSettings(
            GlobalSettings.Default with
            {
                VTubeStudio = new VTubeStudioConnectionSettings("localhost", 8002, "vts.custom"),
            });
        var profile = configuration.ActiveProfile.WithAssignment(
            NumLockLayer.On,
            DeckKey.Numpad8,
            new KeyAssignment(
                "Помахать",
                IconReference.Emoji("👋"),
                new VTubeStudioActionDefinition(
                    VTubeStudioActionKind.TriggerHotkey,
                    "hotkey-wave",
                    "Wave")));

        await store.SaveAsync(configuration.ReplaceProfile(profile));
        var loaded = await store.LoadAsync();

        Assert.AreEqual(8002, loaded.Settings.VTubeStudio.Port);
        Assert.AreEqual("vts.custom", loaded.Settings.VTubeStudio.CredentialKey);
        var action = Assert.IsInstanceOfType<VTubeStudioActionDefinition>(
            loaded.ActiveProfile.NumLockOn.GetAssignment(DeckKey.Numpad8).Action);
        Assert.AreEqual(VTubeStudioActionKind.TriggerHotkey, action.Action);
        Assert.AreEqual("hotkey-wave", action.TargetId);
        Assert.AreEqual("Wave", action.TargetName);
    }

    [TestMethod]
    public async Task LoadAsync_OldConfigurationWithoutVTubeStudioSettings_UsesDefaults()
    {
        using var store = new JsonConfigurationStore(paths);
        await store.SaveAsync(AppConfiguration.CreateDefault());
        var root = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        root["configuration"]!["settings"]!.AsObject().Remove("vTubeStudio");
        File.WriteAllText(paths.ConfigurationFilePath, root.ToJsonString());

        var loaded = await store.LoadAsync();

        Assert.AreEqual("127.0.0.1", loaded.Settings.VTubeStudio.Host);
        Assert.AreEqual(8001, loaded.Settings.VTubeStudio.Port);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesAutomationStepsAndNestedActions()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault();
        var automation = new AutomationActionDefinition(
            ImmutableArray.Create<AutomationStepDefinition>(
                new ExecuteAutomationStepDefinition(new ToggleMicrophoneMuteActionDefinition()),
                new PauseAutomationStepDefinition(1_250),
                new ExecuteAutomationStepDefinition(new ObsActionDefinition(ObsActionKind.SwitchScene, "Gameplay")),
                new FinishAutomationStepDefinition()));
        var profile = configuration.ActiveProfile.WithAssignment(
            NumLockLayer.On,
            DeckKey.Numpad0,
            new KeyAssignment("Начать", IconReference.BuiltIn("play"), automation));

        await store.SaveAsync(configuration.ReplaceProfile(profile));
        var loaded = await store.LoadAsync();

        var loadedAutomation = Assert.IsInstanceOfType<AutomationActionDefinition>(
            loaded.ActiveProfile.NumLockOn.GetAssignment(DeckKey.Numpad0).Action);
        Assert.HasCount(4, loadedAutomation.Steps);
        Assert.IsInstanceOfType<ToggleMicrophoneMuteActionDefinition>(
            Assert.IsInstanceOfType<ExecuteAutomationStepDefinition>(loadedAutomation.Steps[0]).Action);
        Assert.AreEqual(
            1_250,
            Assert.IsInstanceOfType<PauseAutomationStepDefinition>(loadedAutomation.Steps[1]).DurationMilliseconds);
        Assert.AreEqual(
            "Gameplay",
            Assert.IsInstanceOfType<ObsActionDefinition>(
                Assert.IsInstanceOfType<ExecuteAutomationStepDefinition>(loadedAutomation.Steps[2]).Action).TargetName);
        Assert.IsInstanceOfType<FinishAutomationStepDefinition>(loadedAutomation.Steps[3]);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesSystemAudioAction()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault();
        var action = new AdjustApplicationVolumeActionDefinition(
            "msedge.exe",
            VolumeAdjustmentDirection.Decrease,
            8);
        var profile = configuration.ActiveProfile.WithAssignment(
            NumLockLayer.On,
            DeckKey.NumpadSubtract,
            new KeyAssignment("Браузер тише", IconReference.BuiltIn("volume"), action));

        await store.SaveAsync(configuration.ReplaceProfile(profile));
        var loaded = await store.LoadAsync();

        var loadedAction = Assert.IsInstanceOfType<AdjustApplicationVolumeActionDefinition>(
            loaded.ActiveProfile.NumLockOn.GetAssignment(DeckKey.NumpadSubtract).Action);
        Assert.AreEqual("msedge.exe", loadedAction.ApplicationId);
        Assert.AreEqual(VolumeAdjustmentDirection.Decrease, loadedAction.Direction);
        Assert.AreEqual(8, loadedAction.StepPercent);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesIndependentCaptureTargets()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault();
        configuration = configuration.WithSettings(configuration.Settings with
        {
            CaptureNumpad = false,
            CaptureNavigationBlock = true,
        });

        await store.SaveAsync(configuration);
        var loaded = await store.LoadAsync();

        Assert.IsFalse(loaded.Settings.CaptureNumpad);
        Assert.IsTrue(loaded.Settings.CaptureNavigationBlock);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesSelectedKeyboardDevice()
    {
        using var store = new JsonConfigurationStore(paths);
        var selectedDeviceId = @"\\?\HID#VID_1234&PID_5678#7&1234567&0&0000";
        var configuration = AppConfiguration.CreateDefault().WithSettings(
            GlobalSettings.Default with { SelectedKeyboardDeviceId = selectedDeviceId });

        await store.SaveAsync(configuration);
        var loaded = await store.LoadAsync();

        Assert.AreEqual(selectedDeviceId, loaded.Settings.SelectedKeyboardDeviceId);
    }

    [TestMethod]
    public async Task LoadAsync_OldConfigurationWithoutCaptureTargets_EnablesBothGroups()
    {
        using var store = new JsonConfigurationStore(paths);
        await store.SaveAsync(AppConfiguration.CreateDefault());
        var root = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        var settings = root["configuration"]!["settings"]!.AsObject();
        settings.Remove("captureNumpad");
        settings.Remove("captureNavigationBlock");
        File.WriteAllText(
            paths.ConfigurationFilePath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var loaded = await store.LoadAsync();

        Assert.IsTrue(loaded.Settings.CaptureNumpad);
        Assert.IsTrue(loaded.Settings.CaptureNavigationBlock);
    }

    [TestMethod]
    public async Task LoadAsync_OldConfigurationWithoutMuteOverlaySetting_EnablesIndicator()
    {
        using var store = new JsonConfigurationStore(paths);
        await store.SaveAsync(AppConfiguration.CreateDefault());
        var root = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        root["configuration"]!["settings"]!.AsObject().Remove("showMuteOverlay");
        File.WriteAllText(paths.ConfigurationFilePath, root.ToJsonString());

        var loaded = await store.LoadAsync();

        Assert.IsTrue(loaded.Settings.ShowMuteOverlay);
    }

    [TestMethod]
    public async Task LoadAsync_RemovesObsoleteNumpadBackAssignmentsAndPersistsCleanConfiguration()
    {
        using var store = new JsonConfigurationStore(paths);
        var configuration = AppConfiguration.CreateDefault()
            .AddProfile(DeckProfile.CreateDefault("Second profile"), makeActive: false);
        var preservedAssignment = new KeyAssignment(
            "Keep this",
            IconReference.BuiltIn("star"),
            new OpenUriActionDefinition("https://example.com"));
        var firstProfile = configuration.Profiles[0].WithAssignment(
            NumLockLayer.On,
            DeckKey.NumpadSubtract,
            preservedAssignment);
        configuration = configuration.ReplaceProfile(firstProfile);
        await store.SaveAsync(configuration);

        var root = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        foreach (var profile in root["configuration"]!["profiles"]!.AsArray())
        {
            foreach (var layerName in new[] { "numLockOff", "numLockOn" })
            {
                var assignments = profile![layerName]!["assignments"]!.AsObject();
                assignments["numpadBack"] = new JsonObject
                {
                    ["label"] = "Legacy Back",
                    ["icon"] = new JsonObject
                    {
                        ["kind"] = "builtIn",
                        ["value"] = "star",
                    },
                    ["action"] = new JsonObject
                    {
                        ["type"] = "none",
                    },
                };
            }
        }

        File.WriteAllText(paths.ConfigurationFilePath, root.ToJsonString());

        var loaded = await store.LoadAsync();

        Assert.HasCount(2, loaded.Profiles);
        foreach (var profile in loaded.Profiles)
        {
            Assert.HasCount(23, profile.NumLockOff.Assignments);
            Assert.HasCount(23, profile.NumLockOn.Assignments);
        }

        Assert.AreEqual(
            preservedAssignment,
            loaded.Profiles[0].NumLockOn.GetAssignment(DeckKey.NumpadSubtract));

        var cleanedRoot = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        foreach (var profile in cleanedRoot["configuration"]!["profiles"]!.AsArray())
        {
            Assert.IsFalse(profile!["numLockOff"]!["assignments"]!.AsObject().ContainsKey("numpadBack"));
            Assert.IsFalse(profile["numLockOn"]!["assignments"]!.AsObject().ContainsKey("numpadBack"));
        }
    }

    [TestMethod]
    public async Task LoadAsync_AddsNumLockAssignmentsAndDefaultsMissingBehavior()
    {
        using var store = new JsonConfigurationStore(paths);
        await store.SaveAsync(AppConfiguration.CreateDefault());
        var root = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        root["configuration"]!["settings"]!.AsObject().Remove("numLockBehavior");
        foreach (var profile in root["configuration"]!["profiles"]!.AsArray())
        {
            foreach (var layerName in new[] { "numLockOff", "numLockOn" })
            {
                profile![layerName]!["assignments"]!.AsObject().Remove("numLock");
            }
        }

        File.WriteAllText(paths.ConfigurationFilePath, root.ToJsonString());

        var loaded = await store.LoadAsync();

        Assert.AreEqual(
            StreamNumDeck.Core.Settings.NumLockBehavior.SwitchLayers,
            loaded.Settings.NumLockBehavior);
        foreach (var profile in loaded.Profiles)
        {
            Assert.HasCount(23, profile.NumLockOff.Assignments);
            Assert.HasCount(23, profile.NumLockOn.Assignments);
            Assert.AreEqual(KeyAssignment.Empty, profile.NumLockOff.GetAssignment(DeckKey.NumLock));
            Assert.AreEqual(KeyAssignment.Empty, profile.NumLockOn.GetAssignment(DeckKey.NumLock));
        }

        var migratedRoot = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        Assert.IsTrue(migratedRoot["configuration"]!["profiles"]![0]!["numLockOn"]!["assignments"]!
            .AsObject().ContainsKey("numLock"));
    }

    [TestMethod]
    public async Task LoadAsync_PreservesNumLockActionBehavior()
    {
        using var store = new JsonConfigurationStore(paths);
        var settings = GlobalSettings.Default with
        {
            NumLockBehavior = NumLockBehavior.ActionKey,
        };
        await store.SaveAsync(AppConfiguration.CreateDefault().WithSettings(settings));

        var loaded = await store.LoadAsync();

        Assert.AreEqual(NumLockBehavior.ActionKey, loaded.Settings.NumLockBehavior);
    }

    [TestMethod]
    public async Task LoadAsync_NoActionAssignment_DiscardsStalePresentation()
    {
        using var store = new JsonConfigurationStore(paths);
        await store.SaveAsync(AppConfiguration.CreateDefault());
        var root = JsonNode.Parse(File.ReadAllText(paths.ConfigurationFilePath))!.AsObject();
        var assignment = root["configuration"]!["profiles"]![0]!["numLockOff"]!["assignments"]!["insert"]!;
        assignment["label"] = "Старая подпись";
        assignment["icon"]!["value"] = "play";
        File.WriteAllText(
            paths.ConfigurationFilePath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var loaded = await store.LoadAsync();
        var normalized = loaded.ActiveProfile.NumLockOff.GetAssignment(DeckKey.Insert);

        Assert.AreEqual(string.Empty, normalized.Label);
        Assert.AreEqual(IconReference.BuiltIn("square"), normalized.Icon);
        Assert.IsInstanceOfType<NoActionDefinition>(normalized.Action);
    }

    [TestMethod]
    public async Task LoadAsync_WhenPrimaryIsCorrupt_RecoversBackupAndRepairsPrimary()
    {
        using var store = new JsonConfigurationStore(paths);
        var original = AppConfiguration.CreateDefault();
        await store.SaveAsync(original);

        var renamedProfile = new DeckProfile(
            original.ActiveProfile.Id,
            "Изменённый профиль",
            original.ActiveProfile.NumLockOff,
            original.ActiveProfile.NumLockOn);
        var renamedConfiguration = original.ReplaceProfile(renamedProfile);
        await store.SaveAsync(renamedConfiguration);

        var latestProfile = new DeckProfile(
            original.ActiveProfile.Id,
            "Последний профиль",
            original.ActiveProfile.NumLockOff,
            original.ActiveProfile.NumLockOn);
        await store.SaveAsync(original.ReplaceProfile(latestProfile));
        File.WriteAllText(paths.ConfigurationFilePath, "{ invalid json");

        var recovered = await store.LoadAsync();
        var loadedAgain = await store.LoadAsync();

        Assert.AreEqual("Изменённый профиль", recovered.ActiveProfile.Name);
        Assert.AreEqual("Изменённый профиль", loadedAgain.ActiveProfile.Name);
        Assert.IsTrue(File.Exists(paths.BackupFilePath));
    }

    [TestMethod]
    public async Task LoadAsync_WhenSchemaIsUnsupported_ReportsVersionFailure()
    {
        Directory.CreateDirectory(paths.DirectoryPath);
        File.WriteAllText(
            paths.ConfigurationFilePath,
            """
            {
              "schemaVersion": 99,
              "configuration": {}
            }
            """);
        using var store = new JsonConfigurationStore(paths);

        var exception = await Assert.ThrowsAsync<ConfigurationLoadException>(() => store.LoadAsync());

        var versionFailure = Assert.IsInstanceOfType<UnsupportedConfigurationVersionException>(exception.PrimaryFailure);
        Assert.AreEqual(99, versionFailure.Version);
        Assert.AreEqual(ConfigurationSchema.CurrentVersion, versionFailure.CurrentVersion);
    }
}
