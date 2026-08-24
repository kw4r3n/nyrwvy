namespace Nayamnyam.Core;

public enum SnapshotPublishResult { Published, StaleBase, SnapshotIdCollision, InvalidParent }

public sealed class InMemorySnapshotStore
{
    private readonly object gate = new();
    private readonly HashSet<StableId> knownIds;
    private Snapshot current;

    public InMemorySnapshotStore(Snapshot currentSnapshot, IEnumerable<StableId>? knownSnapshotIds = null)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        current = currentSnapshot;
        knownIds = new HashSet<StableId>(knownSnapshotIds ?? []);
        knownIds.Add(current.Id);
        if (current.ParentId is { } parentId) knownIds.Add(parentId);
        if (knownIds.Any(id => id.Domain != IdDomain.Snapshot)) throw new ArgumentException("Known IDs must be snapshot IDs.", nameof(knownSnapshotIds));
    }

    public Snapshot Current { get { lock (gate) return current; } }
    public bool Contains(StableId id) { lock (gate) return knownIds.Contains(id); }

    public SnapshotPublishResult TryPublish(StableId expectedBaseId, Snapshot proposed)
    {
        lock (gate)
        {
            if (current.Id != expectedBaseId) return SnapshotPublishResult.StaleBase;
            if (proposed.ParentId != expectedBaseId) return SnapshotPublishResult.InvalidParent;
            if (knownIds.Contains(proposed.Id)) return SnapshotPublishResult.SnapshotIdCollision;
            knownIds.Add(proposed.Id);
            current = proposed;
            return SnapshotPublishResult.Published;
        }
    }
}
