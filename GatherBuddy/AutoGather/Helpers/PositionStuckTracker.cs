using System;
using System.Numerics;

namespace GatherBuddy.AutoGather.Helpers;

public sealed class PositionStuckTracker
{
    private Vector3  _anchor;
    private DateTime _enteredAt;

    public bool IsTracking { get; private set; }

    public void Start(Vector3 position)
    {
        _anchor    = position;
        _enteredAt = DateTime.UtcNow;
        IsTracking = true;
    }

    public bool Update(Vector3 position, float radius)
    {
        if (!IsTracking)
            return false;

        if (Vector3.Distance(position, _anchor) <= radius)
            return false;

        // Leaving the watched area proves that the character made meaningful progress.
        // Re-anchor instead of keeping an obsolete centre forever.
        Start(position);
        return true;
    }

    public double ElapsedSeconds
        => IsTracking ? (DateTime.UtcNow - _enteredAt).TotalSeconds : 0.0;

    public void Reset()
    {
        IsTracking = false;
        _anchor    = default;
        _enteredAt = DateTime.MinValue;
    }
}
