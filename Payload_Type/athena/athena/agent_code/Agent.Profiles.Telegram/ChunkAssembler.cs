namespace Agent.Profiles;

internal sealed class ChunkAssembler
{
    private const int MaximumChunks = 256;
    private static readonly TimeSpan PacketLifetime = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, PendingPacket> _packets = new(StringComparer.Ordinal);

    public bool TryAdd(TelegramEnvelope envelope, out string message)
    {
        message = string.Empty;
        RemoveExpiredPackets();

        if (envelope.Version != 1 ||
            string.IsNullOrWhiteSpace(envelope.PacketId) ||
            envelope.Chunks < 1 ||
            envelope.Chunks > MaximumChunks ||
            envelope.Chunk < 0 ||
            envelope.Chunk >= envelope.Chunks)
        {
            return false;
        }

        if (!_packets.TryGetValue(envelope.PacketId, out PendingPacket? packet))
        {
            packet = new PendingPacket(envelope.Chunks);
            _packets.Add(envelope.PacketId, packet);
        }

        if (packet.Chunks.Length != envelope.Chunks)
        {
            _packets.Remove(envelope.PacketId);
            return false;
        }

        packet.Chunks[envelope.Chunk] ??= envelope.Message;
        if (packet.Chunks.Any(chunk => chunk is null))
        {
            return false;
        }

        message = string.Concat(packet.Chunks!);
        _packets.Remove(envelope.PacketId);
        return true;
    }

    private void RemoveExpiredPackets()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - PacketLifetime;
        foreach (string packetId in _packets
                     .Where(item => item.Value.CreatedAt < cutoff)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _packets.Remove(packetId);
        }
    }

    private sealed class PendingPacket
    {
        public PendingPacket(int chunks)
        {
            Chunks = new string?[chunks];
        }

        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public string?[] Chunks { get; }
    }
}
