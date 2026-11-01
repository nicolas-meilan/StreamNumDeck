using StreamNumDeck.Core.Actions;
using StreamNumDeck.Core.Configuration;
using StreamNumDeck.Core.Deck;
using StreamNumDeck.Core.Icons;
using StreamNumDeck.Core.Input;
using System.Collections.Immutable;

namespace StreamNumDeck.Core.Tests;

[TestClass]
public sealed class DeckModelTests
{
    [TestMethod]
    public void CaptureTargets_SeparateNavigationBlockFromNumpad()
    {
        foreach (var key in DeckKeyCatalog.AssignableKeys)
        {
            var isNavigationKey = key is
                DeckKey.Insert or DeckKey.Home or DeckKey.PageUp or
                DeckKey.Delete or DeckKey.End or DeckKey.PageDown;

            Assert.AreEqual(isNavigationKey, KeyboardCaptureTargets.NavigationBlock.Includes(key), key.ToString());
            Assert.AreEqual(!isNavigationKey, KeyboardCaptureTargets.Numpad.Includes(key), key.ToString());
        }
    }

    [TestMethod]
    public void DefaultSettings_SelectGlobalKeyboardCapture()
    {
        Assert.AreEqual(string.Empty, StreamNumDeck.Core.Settings.GlobalSettings.Default.SelectedKeyboardDeviceId);
    }

    [TestMethod]
    public void AssignableKeyCatalog_ContainsTwentyThreeUniquePhysicalKeys()
    {
        Assert.HasCount(23, DeckKeyCatalog.AssignableKeys);
        Assert.HasCount(23, DeckKeyCatalog.AssignableKeys.Distinct());
    }

    [TestMethod]
    public void DefaultProfile_ContainsCompleteIndependentLayers()
    {
        var profile = DeckProfile.CreateDefault();

        Assert.AreEqual(NumLockLayer.Off, profile.NumLockOff.Mode);
        Assert.AreEqual(NumLockLayer.On, profile.NumLockOn.Mode);
        Assert.HasCount(23, profile.NumLockOff.Assignments);
        Assert.HasCount(23, profile.NumLockOn.Assignments);
        Assert.AreEqual(IconReference.BuiltIn("broadcast"), profile.Icon);
        Assert.HasCount(AdditionalButtonSettings.Count, profile.AdditionalButtons);
        Assert.IsTrue(profile.AdditionalButtons.All(button => button.VirtualKeyCode is null));
    }

    [TestMethod]
    public void AdditionalButtons_ArePreservedWhenProfileIsDuplicatedAndEdited()
    {
        var profile = DeckProfile.CreateDefault().WithAdditionalButton(
            new AdditionalButtonSettings(2, 0x41, KeyAssignment.Empty));
        var duplicate = profile.Duplicate("copy");

        Assert.AreEqual((uint)0x41, duplicate.AdditionalButtons[2].VirtualKeyCode);
        Assert.AreEqual(8, duplicate.AdditionalButtons.Length);
        Assert.AreNotEqual(profile.Id, duplicate.Id);
    }

    [TestMethod]
    public void KeyAssignment_OnlyAllowsF13ThroughF24AsRemappingTargets()
    {
        var assignment = KeyAssignment.Empty with { RemappedVirtualKeyCode = 0x7C };

        Assert.AreEqual((uint)0x7C, assignment.RemappedVirtualKeyCode);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = KeyAssignment.Empty with { RemappedVirtualKeyCode = 0x7B });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = KeyAssignment.Empty with { RemappedVirtualKeyCode = 0x88 });
    }

    [TestMethod]
    public void NoActionAssignment_DiscardsStalePresentation()
    {
        var assignment = new KeyAssignment(
            "Старая подпись",
            IconReference.CustomAsset("icons/stale.png"),
            new NoActionDefinition());

        Assert.AreEqual(string.Empty, assignment.Label);
        Assert.AreEqual(IconReference.BuiltIn("square"), assignment.Icon);
        Assert.IsInstanceOfType<NoActionDefinition>(assignment.Action);
    }

    [TestMethod]
    public void DeviceIdentification_IsDisabledByDefaultAndStoredOnAssignment()
    {
        var assignment = new KeyAssignment(
            "Block by device",
            IconReference.BuiltIn("play"),
            new OpenPathActionDefinition("path"))
        {
            IdentifySelectedKeyboardBeforeBlocking = true,
        };

        Assert.IsFalse(KeyAssignment.Empty.IdentifySelectedKeyboardBeforeBlocking);
        Assert.IsTrue(assignment.IdentifySelectedKeyboardBeforeBlocking);
    }

    [TestMethod]
    public void AddProfile_AppendsAndCanMakeProfileActive()
    {
        var configuration = AppConfiguration.CreateDefault();
        var profile = DeckProfile.CreateDefault("Игровой", IconReference.BuiltIn("play"));

        var updated = configuration.AddProfile(profile);

        Assert.HasCount(2, updated.Profiles);
        Assert.AreEqual(profile.Id, updated.ActiveProfileId);
        Assert.AreEqual(IconReference.BuiltIn("play"), updated.ActiveProfile.Icon);
    }

    [TestMethod]
    public void RemoveProfile_RemovesRequestedProfileAndSelectsRemainingProfile()
    {
        var configuration = AppConfiguration.CreateDefault();
        var second = DeckProfile.CreateDefault("Игровой");
        configuration = configuration.AddProfile(second);

        var updated = configuration.RemoveProfile(second.Id);

        Assert.HasCount(1, updated.Profiles);
        Assert.AreNotEqual(second.Id, updated.ActiveProfileId);
        Assert.AreEqual("Основной стрим", updated.ActiveProfile.Name);
    }

    [TestMethod]
    public void RemoveProfile_RejectsRemovingLastProfile()
    {
        var configuration = AppConfiguration.CreateDefault();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            configuration.RemoveProfile(configuration.ActiveProfileId));
    }

    [TestMethod]
    public void Duplicate_CopiesAssignmentsWithNewIdentity()
    {
        var source = DeckProfile.CreateDefault("Игровой", IconReference.BuiltIn("game"))
            .WithAssignment(
                NumLockLayer.On,
                DeckKey.Numpad0,
                new KeyAssignment("Сайт", IconReference.BuiltIn("globe"), new OpenUriActionDefinition("https://example.com")));

        var duplicate = source.Duplicate("Игровой — копия");

        Assert.AreNotEqual(source.Id, duplicate.Id);
        Assert.AreEqual(source.NumLockOff, duplicate.NumLockOff);
        Assert.AreEqual(source.NumLockOn, duplicate.NumLockOn);
        Assert.AreEqual(source.Icon, duplicate.Icon);
    }

    [TestMethod]
    public void WithAssignment_ChangesOnlyRequestedLayerAndKey()
    {
        var profile = DeckProfile.CreateDefault();
        var assignment = new KeyAssignment(
            "Аплодисменты",
            IconReference.BuiltIn("music"),
            new PlaySoundActionDefinition("C:\\Sounds\\applause.wav", 80));

        var updatedProfile = profile.WithAssignment(NumLockLayer.Off, DeckKey.Numpad7, assignment);

        Assert.AreEqual(assignment, updatedProfile.NumLockOff.GetAssignment(DeckKey.Numpad7));
        Assert.IsInstanceOfType<NoActionDefinition>(updatedProfile.NumLockOn.GetAssignment(DeckKey.Numpad7).Action);
        Assert.IsInstanceOfType<NoActionDefinition>(updatedProfile.NumLockOff.GetAssignment(DeckKey.Numpad8).Action);
    }

    [TestMethod]
    public void DeckLayer_RejectsIncompleteAssignments()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DeckLayer(NumLockLayer.Off, ImmutableDictionary<DeckKey, KeyAssignment>.Empty));
    }

    [TestMethod]
    public void AppConfiguration_RejectsUnknownActiveProfile()
    {
        var profile = DeckProfile.CreateDefault();

        Assert.ThrowsExactly<ArgumentException>(() =>
            new AppConfiguration(
                Guid.NewGuid(),
                ImmutableArray.Create(profile),
                StreamNumDeck.Core.Settings.GlobalSettings.Default));
    }

    [TestMethod]
    public void SystemAudioActions_ValidateAndNormalizeParameters()
    {
        var application = new AdjustApplicationVolumeActionDefinition(
            @"C:\Program Files\Google\Chrome\chrome.exe",
            VolumeAdjustmentDirection.Decrease,
            7);
        var master = new AdjustMasterVolumeActionDefinition(
            VolumeAdjustmentDirection.Increase,
            5);

        Assert.AreEqual("chrome.exe", application.ApplicationId);
        Assert.AreEqual(7, application.StepPercent);
        Assert.AreEqual(5, master.StepPercent);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AdjustMasterVolumeActionDefinition(VolumeAdjustmentDirection.Increase, 0));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AdjustApplicationVolumeActionDefinition(" ", VolumeAdjustmentDirection.Increase, 5));
    }
}
