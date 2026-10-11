using System.Buffers.Binary;
using System.Net;

namespace WolfSpeak;

public sealed class Peer
{
    public uint Id;
    public string Name = "Friend";
    public IPEndPoint EndPoint = new(IPAddress.Loopback, 50505);
    public bool VersionMatches = true, HelloSeen = true;
    public long LastSeen;
}

// Production chat and crypto, with an in-memory unreliable network and no audio.
public sealed partial class VoiceEngine
{
    const int HeaderSize = 7;
    readonly object callLock = new();
    readonly Dictionary<uint, Peer> peers = [];
    readonly Identity identity = Identity.LoadOrCreate();
    readonly byte[] rxPlain = new byte[2048];
    readonly List<byte[]> sent = [];
    uint myId;
    bool IsDemo;
    Peer? Partner = null;
    public event Action<string>? Notice;
    static string VersionMismatchText(Peer p) => "Version mismatch";
    void WriteHeader(byte[] packet, byte type)
    {
        packet[0] = (byte)'W'; packet[1] = (byte)'S'; packet[2] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), myId);
    }
    void Send(byte[] packet, IPEndPoint ep) => sent.Add(packet);
    void SendControl(byte type, Peer peer, byte[]? payload = null)
    {
        var packet = new byte[HeaderSize + (payload?.Length ?? 0)];
        WriteHeader(packet, type); payload?.CopyTo(packet, HeaderSize); Send(packet, peer.EndPoint);
    }
    static void Deliver(VoiceEngine sender, VoiceEngine recipient)
    {
        var packets = sender.sent.ToArray(); sender.sent.Clear();
        foreach (var packet in packets)
            recipient.OnChatPacket(packet[2], recipient.peers[sender.myId], new(IPAddress.Loopback, 50505), packet);
    }
    static (VoiceEngine A, VoiceEngine B) Pair()
    {
        var a = new VoiceEngine { myId = 1 };
        var b = new VoiceEngine { myId = 2 };
        a.peers[2] = new Peer { Id = 2 }; b.peers[1] = new Peer { Id = 1 };
        return (a, b);
    }
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    static void Main()
    {
        var (a, b) = Pair();
        var incoming = new List<ChatMessage>();
        var delivery = new List<bool>();
        b.ChatReceived += incoming.Add;
        a.ChatDelivery += (_, _, ok) => delivery.Add(ok);
        Check(!a.SendChat(2, " ") && !a.SendChat(2, new string('a', 401)) && !a.SendChat(2, "\uD800"), "invalid input rejected");
        Check(a.Partner is null && a.SendChat(2, "Hello 🐺"), "send without a voice call");
        Check(!a.SendChat(2, "Wait"), "one message per peer in flight");
        var request = a.sent[0];
        Check(!CallCrypto.VerifyRequest(request.AsSpan(7), 1, out _, out _), "chat handshake cannot be used as a call request");
        a.sent.Clear(); // Drop first request.
        a.TickChat(a.chats[2].LastSent + 501);
        Deliver(a, b); // Request.
        Check(b.sent.Count == 1 && incoming.Count == 0, "handshake does not deliver plaintext");
        b.sent.Clear(); // Drop accept; duplicate request must recover it.
        b.chats[1].LastRequest -= 501;
        a.TickChat(a.chats[2].LastSent + 501);
        Deliver(a, b); Deliver(b, a); Deliver(a, b);
        Check(incoming.Count == 1 && incoming[0].Text == "Hello 🐺", "lost handshake recovers and decrypts message");
        b.sent.Clear(); // Drop ack.
        a.TickChat(a.chats[2].LastSent + 501);
        Deliver(a, b); Deliver(b, a);
        Check(incoming.Count == 1 && delivery.Single() && !a.ChatPending(2), "lost acknowledgment retries without duplicate messages");
        a.SendChat(2, new string('界', 400));
        var corrupt = a.sent[0].ToArray(); corrupt[^1] ^= 1;
        b.OnChatPacket(TypeChatSecure, b.peers[1], b.peers[1].EndPoint, corrupt);
        Check(incoming.Count == 1, "tampered ciphertext rejected");
        var validPacket = a.sent[0].ToArray();
        Deliver(a, b); Deliver(b, a);
        Check(incoming.Count == 2 && incoming[^1].Text.Length == 400, "maximum UTF-8 message delivered");
        b.OnChatPacket(TypeChatSecure, b.peers[1], b.peers[1].EndPoint, validPacket);
        Check(incoming.Count == 2, "encrypted packet replay rejected");
        a.SendChatSecure(a.peers[2], a.chats[2], TypeChat, new byte[] { 3, 0, 0, 0, 0xff });
        Deliver(a, b);
        Check(incoming.Count == 2, "malformed UTF-8 rejected");
        a.SendChat(2, "Timeout"); a.TickChat(a.chats[2].Started + 10001);
        Check(!a.ChatPending(2) && !delivery[^1], "timeout reports unconfirmed delivery");
        a.peers[2].VersionMatches = false;
        Check(!a.SendChat(2, "Old version"), "version mismatch rejected");
        Check(!a.SendChat(99, "Offline"), "offline recipient rejected");
        var (c, d) = Pair();
        var simultaneous = new List<ChatMessage>();
        c.ChatReceived += simultaneous.Add; d.ChatReceived += simultaneous.Add;
        c.SendChat(2, "From A"); d.SendChat(1, "From B");
        for (int i = 0; i < 5; i++) { Deliver(c, d); Deliver(d, c); }
        Check(simultaneous.Count(m => !m.Outgoing) == 2 && !c.ChatPending(2) && !d.ChatPending(1), "simultaneous first messages use one encrypted session");
        c.SendChat(2, "Disconnected"); c.RemoveChat(2);
        Check(!c.ChatPending(2), "disconnect clears pending delivery");
        var demo = new VoiceEngine { IsDemo = true }; demo.peers[2] = new() { Id = 2 };
        Check(demo.SendChat(2, "Demo") && !demo.ChatPending(2) && demo.sent.Count == 0, "demo sends no network traffic");
    }
}
