using System.Buffers.Binary;

namespace H5.Net.Transport;

/// <summary>
/// Datagram kinds sharing one UDP socket: the reliable channel protocol, NAT
/// hole-punch probes, rendezvous messages exchanged with the master server, and
/// relay envelopes forwarded by the master when punching fails.
/// </summary>
public enum UdpKind : byte
{
    Connect = 0x01,     // [connId u32][magic u32]
    Accept = 0x02,      // [connId u32]
    Data = 0x03,        // [connId u32][seq u32][frag u16][fragCount u16][payload]
    Ack = 0x04,         // [connId u32][nextExpected u32][sack u64]
    Ping = 0x05,        // [connId u32][ticks i64]
    Pong = 0x06,        // [connId u32][ticks i64]
    Close = 0x07,       // [connId u32]
    Punch = 0x08,       // [token u64]  hole-punch probe
    PunchAck = 0x09,    // [token u64]
    Rendezvous = 0x20,  // [utf8 json] master <-> peer control messages
    Relay = 0x30,       // [session u64][inner datagram]
}

public static class UdpPacket
{
    public const uint Magic = 0x48354E54; // "H5NT"
    public const int MaxDatagram = 1200;
    public const int DataHeader = 1 + 4 + 4 + 2 + 2;
    public const int MaxFragmentPayload = MaxDatagram - DataHeader - 1 - 8; // leave room for a relay envelope

    public static byte[] Control(UdpKind kind, uint connId, int extra = 0)
    {
        var b = new byte[5 + extra];
        b[0] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), connId);
        return b;
    }

    public static byte[] Data(uint connId, uint seq, ushort frag, ushort count, ReadOnlySpan<byte> payload)
    {
        var b = new byte[DataHeader + payload.Length];
        b[0] = (byte)UdpKind.Data;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), connId);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), seq);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(9), frag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(11), count);
        payload.CopyTo(b.AsSpan(DataHeader));
        return b;
    }

    public static byte[] Relay(ulong session, ReadOnlySpan<byte> inner)
    {
        var b = new byte[9 + inner.Length];
        b[0] = (byte)UdpKind.Relay;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(1), session);
        inner.CopyTo(b.AsSpan(9));
        return b;
    }

    public static byte[] Token(UdpKind kind, ulong token)
    {
        var b = new byte[9];
        b[0] = (byte)kind;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(1), token);
        return b;
    }
}
