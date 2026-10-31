using StreamNumDeck.Infrastructure.Input;
using StreamNumDeck.Core.Deck;

namespace StreamNumDeck.Infrastructure.Tests;

[TestClass]
public sealed class RawKeyboardInputCorrelatorTests
{
    [TestMethod]
    public void WaitForKey_MatchesKeyAndConsumesInput()
    {
        var correlator = new RawKeyboardInputCorrelator();
        correlator.Publish(DeckKey.NumpadBack, true);

        var matched = correlator.WaitForKey(DeckKey.NumpadBack, true);

        Assert.IsTrue(matched);
    }

    [TestMethod]
    public void WaitForKey_RejectsDifferentTransitionAndPassesAfterTimeout()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), RawKeyboardInputCorrelator.Timeout);
        var correlator = new RawKeyboardInputCorrelator();
        correlator.Publish(DeckKey.NumpadBack, true);
        var started = DateTime.UtcNow;

        var matched = correlator.WaitForKey(DeckKey.NumpadBack, false);

        Assert.IsFalse(matched);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(100));
    }

    [TestMethod]
    public void WaitForKey_DoesNotMatchDifferentDeckKey()
    {
        var correlator = new RawKeyboardInputCorrelator();
        correlator.Publish(DeckKey.NumpadBack, true);

        var matched = correlator.WaitForKey(DeckKey.NumpadEnter, true);

        Assert.IsFalse(matched);
    }
}
