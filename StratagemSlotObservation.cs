namespace HD2_Helper;

internal enum StratagemSlotState { Unknown, Empty, Equipped }
internal sealed record StratagemSlotObservation(int SlotIndex, StratagemSlotState State, string? Name)
{
    public bool Known => State is StratagemSlotState.Empty or StratagemSlotState.Equipped;

    public static StratagemSlotObservation FromEvidence(int slot, bool borderLocated, string? matchedName, int foregroundPixels)
    {
        if (!borderLocated || slot is < 0 or > 3)
            return new(slot, StratagemSlotState.Unknown, null);
        if (!string.IsNullOrWhiteSpace(matchedName))
            return new(slot, StratagemSlotState.Equipped, matchedName);
        // Failure to identify a visible icon is never evidence that the slot is empty.
        return new(slot, foregroundPixels == 0 ? StratagemSlotState.Empty : StratagemSlotState.Unknown, null);
    }
}

internal sealed class StratagemSlotConsensus
{
    private StratagemSlotObservation? _previous;
    private long _previousAt;
    private int _count;

    public bool Observe(StratagemSlotObservation observation, int expectedSlot, long now)
    {
        if (!observation.Known || observation.SlotIndex != expectedSlot)
        {
            _previous = null;
            _count = 0;
            return false;
        }
        long elapsed = now - _previousAt;
        _count = observation == _previous && elapsed >= 25 && elapsed <= 250 ? _count + 1 : 1;
        _previous = observation;
        _previousAt = now;
        // Absence of content needs one more observation than a positive icon match.
        return _count >= (observation.State == StratagemSlotState.Empty ? 3 : 2);
    }
}
