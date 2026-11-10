using DemoFile;
using Minerva.GameState.Live;
using Minerva.GameState.Model;
using Minerva.GameState.StaticData;

namespace Minerva.GameState;

/// <summary>
/// One parse of one demo (finished file or live recording). Raises <see cref="EntitiesUpdated"/> after every network
/// update has been applied, which is the point to take a <see cref="Snapshot"/>.
/// </summary>
public sealed class MatchSession
{
    public MatchSession(GameData data)
    {
        Data = data;
        Builder = new SnapshotBuilder(data);
        // Registered after DemoFile's own handler, so entity state is already updated when this fires.
        Parser.PacketEvents.SvcPacketEntities += _ => EntitiesUpdated?.Invoke();
    }

    public DeadlockDemoParser Parser { get; } = new();
    public GameData Data { get; }
    public SnapshotBuilder Builder { get; }

    /// <summary>Fired once per network update (up to 64 Hz), after entities have been updated.</summary>
    public event Action? EntitiesUpdated;

    public GameSnapshot Snapshot() => Builder.Build(Parser);

    public Task ReadFileAsync(string path, CancellationToken ct = default) =>
        DemoReaders.ReadFileAsync(Parser, path, ct);

    public Task<LiveEndReason> FollowAsync(string path, LiveOptions? options = null,
        Action<TailingFileStream>? onStream = null, CancellationToken ct = default) =>
        DemoReaders.FollowAsync(Parser, path, options, onStream, ct);
}
