namespace Nayamnyam.Core;

public sealed class FileSnapshotStore
{
    private readonly string root;
    public FileSnapshotStore(string root) => this.root = root;

    public Snapshot Initialize(Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.Combine(root, "snapshots"));
        if (File.Exists(Path.Combine(root, "CURRENT"))) throw new InvalidOperationException("The store is already initialized.");
        WriteSnapshot(snapshot);
        WriteCurrent(snapshot.Id);
        return Load();
    }

    public Snapshot Load()
    {
        var id = StableId.Parse(File.ReadAllText(Path.Combine(root, "CURRENT")).Trim());
        var bytes = File.ReadAllText(SnapshotPath(id));
        var snapshot = SnapshotSerializer.Deserialize(bytes);
        if (snapshot.Id != id || SnapshotSerializer.Serialize(snapshot) != bytes) throw new InvalidDataException("The stored snapshot is not canonical.");
        return snapshot;
    }

    public void Publish(StableId expectedBaseId, Snapshot proposed)
    {
        using var lockFile = new FileStream(Path.Combine(root, "LOCK"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Load().Id != expectedBaseId) throw new InvalidOperationException("The durable base snapshot is stale.");
        if (proposed.ParentId != expectedBaseId) throw new InvalidOperationException("The proposed snapshot has the wrong parent.");
        WriteSnapshot(proposed);
        WriteCurrent(proposed.Id);
    }

    private void WriteSnapshot(Snapshot snapshot)
    {
        var path = SnapshotPath(snapshot.Id);
        if (File.Exists(path)) throw new InvalidOperationException("Snapshot ID already exists.");
        WriteAtomic(path, SnapshotSerializer.Serialize(snapshot));
    }

    private void WriteCurrent(StableId id) => WriteAtomic(Path.Combine(root, "CURRENT"), id + "\n");
    private string SnapshotPath(StableId id) => Path.Combine(root, "snapshots", $"{id.Value:D}.json");
    private static void WriteAtomic(string path, string content)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream)) { writer.Write(content); writer.Flush(); stream.Flush(true); }
        File.Move(temporary, path, overwrite: true);
    }
}
