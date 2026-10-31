using System.Diagnostics;
using StreamNumDeck.Core.Deck;

namespace StreamNumDeck.Infrastructure.Input;

internal sealed class RawKeyboardInputCorrelator
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(10);
    private const int MaximumPendingInputs = 64;
    private static readonly long TimeoutTicks = (long)(Timeout.TotalSeconds * Stopwatch.Frequency);

    private readonly object gate = new();
    private readonly List<RawKeyboardInput> pendingInputs = [];

    public void Publish(DeckKey key, bool isKeyDown)
    {
        lock (gate)
        {
            var receivedAt = Stopwatch.GetTimestamp();
            RemoveExpired(receivedAt);
            if (pendingInputs.Count == MaximumPendingInputs)
            {
                pendingInputs.RemoveAt(0);
            }

            pendingInputs.Add(new RawKeyboardInput(key, isKeyDown, receivedAt));
            Monitor.PulseAll(gate);
        }
    }

    public bool WaitForKey(DeckKey key, bool isKeyDown)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
        lock (gate)
        {
            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                RemoveExpired(now);
                var matchIndex = FindBestMatch(key, isKeyDown, now);
                if (matchIndex >= 0)
                {
                    pendingInputs.RemoveAt(matchIndex);
                    return true;
                }

                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return false;
                }

                Monitor.Wait(gate, TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency));
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            pendingInputs.Clear();
        }
    }

    private int FindBestMatch(
        DeckKey key,
        bool isKeyDown,
        long now)
    {
        var bestIndex = -1;
        var bestAge = long.MaxValue;
        for (var index = 0; index < pendingInputs.Count; index++)
        {
            var input = pendingInputs[index];
            if (input.Key != key || input.IsKeyDown != isKeyDown)
            {
                continue;
            }

            var age = now - input.ReceivedAt;
            if (age <= TimeoutTicks && age < bestAge)
            {
                bestIndex = index;
                bestAge = age;
            }
        }

        return bestIndex;
    }

    private void RemoveExpired(long now) =>
        pendingInputs.RemoveAll(input => now - input.ReceivedAt > TimeoutTicks);

    private sealed record RawKeyboardInput(DeckKey Key, bool IsKeyDown, long ReceivedAt);
}
