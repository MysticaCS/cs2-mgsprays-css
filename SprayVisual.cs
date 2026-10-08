namespace MgSprays;

internal sealed record SpraySnapshot(ulong Owner, string Material, float Width, float Height, float Depth,
    float X, float Y, float Z, float Pitch, float Yaw, float Roll, double PlacedAt);

internal enum VisualRefresh { Unchanged, Recreated, Expired }

// The saved spray outlives the engine entity. Round cleanup may invalidate the
// entity, but must never reset placement time or discard ownership/appearance.
internal sealed class SprayVisual<T>(SpraySnapshot snapshot) where T : class
{
    public SpraySnapshot Snapshot { get; } = snapshot;
    public T? Entity { get; private set; }

    public VisualRefresh Refresh(double now, float lifetime, bool force,
        Func<T, bool> valid, Action<T> remove, Func<SpraySnapshot, T> spawn)
    {
        if (CvarRules.Remaining(Snapshot.PlacedAt, now, lifetime) <= 0) return VisualRefresh.Expired;
        if (!force && Entity != null && valid(Entity)) return VisualRefresh.Unchanged;
        if (Entity != null && valid(Entity)) remove(Entity);
        Entity = null;
        Entity = spawn(Snapshot);
        return VisualRefresh.Recreated;
    }

    public void Remove(Action<T> remove, Func<T, bool> valid)
    {
        if (Entity != null && valid(Entity)) remove(Entity);
        Entity = null;
    }
}
