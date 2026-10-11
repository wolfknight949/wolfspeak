using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WolfSpeak;

public sealed record ChatMessage(uint Id, string Sender, string Text, bool Outgoing, uint PeerId = 0);

public sealed partial class VoiceEngine
{
    const byte TypeChatRequest = 13, TypeChatAccept = 14, TypeChatSecure = 15;
    const byte TypeChat = 11, TypeChatAck = 12;
    public const int MaxChatLength = 400;
    static readonly UTF8Encoding ChatEncoding = new(false, true);
    sealed class ChatChannel
    {
        public ECDiffieHellman? Ephemeral;
        public byte[]? PublicKey, Request, AcceptedRequest, Accept;
        public CallSession? Session;
        public string? Fingerprint;
        public uint ReceivedId, PendingId;
        public byte[]? Pending;
        public long Started, LastSent, LastRequest;
    }
    readonly Dictionary<uint, ChatChannel> chats = [];
    uint nextChatId;
    public event Action<ChatMessage>? ChatReceived;
    public event Action<uint, uint, bool>? ChatDelivery;
    public event Action<uint, string>? ChatIdentity;

    public bool SendChat(string text) => Partner is { } p && SendChat(p.Id, text);

    public bool ChatPending(uint peerId)
    {
        lock (callLock) return chats.TryGetValue(peerId, out var c) && c.Pending is not null;
    }

    public bool SendChat(uint peerId, string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > MaxChatLength) return false;
        byte[] encoded;
        try { encoded = ChatEncoding.GetBytes(text); }
        catch (EncoderFallbackException) { return false; }
        lock (callLock)
        {
            if (!peers.TryGetValue(peerId, out var peer)) { Notice?.Invoke("Your friend is offline."); return false; }
            if (!peer.VersionMatches) { Notice?.Invoke(VersionMismatchText(peer)); return false; }
            if (!chats.TryGetValue(peerId, out var c)) chats[peerId] = c = new();
            if (c.Pending is not null) return false;
            uint id = ++nextChatId;
            c.Pending = new byte[4 + encoded.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(c.Pending, id);
            encoded.CopyTo(c.Pending, 4);
            c.PendingId = id;
            c.Started = Environment.TickCount64;
            ChatReceived?.Invoke(new(id, "You", text, true, peerId));
            if (IsDemo) CompleteChat(peerId, c, true);
            else
            {
                if (c.Session is null && c.Request is null)
                {
                    c.Ephemeral = CallCrypto.NewEphemeral();
                    c.PublicKey = c.Ephemeral.ExportSubjectPublicKeyInfo();
                    c.Request = CallCrypto.BuildRequest(identity, myId, c.PublicKey, chat: true);
                }
                SendPendingChat(peer, c, c.Started);
            }
            return true;
        }
    }

    void SendPendingChat(Peer peer, ChatChannel c, long now)
    {
        c.LastSent = now;
        if (c.Session is null) SendControl(TypeChatRequest, peer, c.Request);
        else SendChatSecure(peer, c, TypeChat, c.Pending!);
    }

    void SendChatSecure(Peer peer, ChatChannel c, byte type, ReadOnlySpan<byte> payload)
    {
        var plain = new byte[1 + payload.Length];
        plain[0] = type;
        payload.CopyTo(plain.AsSpan(1));
        var packet = new byte[HeaderSize + CallCrypto.Overhead + plain.Length];
        WriteHeader(packet, TypeChatSecure);
        c.Session!.Seal(packet, HeaderSize, plain);
        Send(packet, peer.EndPoint);
    }

    void OnChatPacket(byte type, Peer peer, System.Net.IPEndPoint ep, ReadOnlySpan<byte> packet)
    {
        lock (callLock)
        {
            if (!peer.HelloSeen || !peer.VersionMatches) return;
            var payload = packet[HeaderSize..];
            chats.TryGetValue(peer.Id, out var c);
            if (type == TypeChatRequest)
            {
                if (payload.Length > 512) return;
                long now = Environment.TickCount64;
                // Repeated handshake packets recover a lost accept without changing keys.
                if (c?.AcceptedRequest is { } accepted && payload.SequenceEqual(accepted))
                {
                    if (now - c.LastRequest >= 250) { c.LastRequest = now; SendControl(TypeChatAccept, peer, c.Accept); }
                    return;
                }
                if (c is not null && now - c.LastRequest < 500) return;
                if (c is not null) c.LastRequest = now;
                if (!CallCrypto.VerifyRequest(payload, peer.Id, out var callerKey, out var remoteIdentity, chat: true)) return;
                // Simultaneous messages: the lower peer id remains the initiator.
                if (c?.Request is not null && c.Session is null && myId < peer.Id) return;
                c ??= new();
                c.LastRequest = now;
                c.Ephemeral?.Dispose();
                using var ephemeral = CallCrypto.NewEphemeral();
                var publicKey = ephemeral.ExportSubjectPublicKeyInfo();
                c.Accept = CallCrypto.BuildAccept(identity, myId, publicKey, callerKey, chat: true);
                c.AcceptedRequest = payload.ToArray();
                c.Request = c.PublicKey = null;
                c.Ephemeral = null;
                c.ReceivedId = 0;
                c.Session?.Dispose();
                c.Session = new CallSession(CallCrypto.DeriveKey(ephemeral, callerKey, callerKey, publicKey, chat: true), false);
                c.Fingerprint = CallCrypto.Fingerprint(remoteIdentity);
                chats[peer.Id] = c;
                ChatIdentity?.Invoke(peer.Id, c.Fingerprint);
                SendControl(TypeChatAccept, peer, c.Accept);
                if (c.Pending is not null) SendPendingChat(peer, c, now);
            }
            else if (type == TypeChatAccept)
            {
                if (c?.Ephemeral is null || c.Session is not null || payload.Length > 512) return;
                if (!CallCrypto.VerifyAccept(payload, peer.Id, c.PublicKey!, out var remoteKey, out var remoteIdentity, chat: true)) return;
                c.Session = new CallSession(CallCrypto.DeriveKey(c.Ephemeral, remoteKey, c.PublicKey!, remoteKey, chat: true), true);
                c.Ephemeral.Dispose(); c.Ephemeral = null; c.Request = null;
                c.Fingerprint = CallCrypto.Fingerprint(remoteIdentity);
                ChatIdentity?.Invoke(peer.Id, c.Fingerprint);
                if (c.Pending is not null) SendPendingChat(peer, c, Environment.TickCount64);
            }
            else if (type == TypeChatSecure && c?.Session is { } session)
            {
                if (!session.TryOpen(packet, HeaderSize, rxPlain, out int length) || length < 1) return;
                peer.EndPoint = ep;
                peer.LastSeen = Environment.TickCount64;
                payload = rxPlain.AsSpan(1, length - 1);
                if (rxPlain[0] == TypeChatAck && payload.Length == 4)
                {
                    if (c.Pending is not null && BinaryPrimitives.ReadUInt32LittleEndian(payload) == c.PendingId)
                        CompleteChat(peer.Id, c, true);
                }
                else if (rxPlain[0] == TypeChat && payload.Length is >= 5 and <= 4 + MaxChatLength * 3)
                {
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    if (id == 0) return;
                    if (id > c.ReceivedId)
                    {
                        string text;
                        try { text = ChatEncoding.GetString(payload[4..]); }
                        catch (DecoderFallbackException) { return; }
                        if (text.Length > MaxChatLength || string.IsNullOrWhiteSpace(text)) return;
                        text = new string(text.Where(ch => !char.IsControl(ch) &&
                            char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format).ToArray());
                        if (string.IsNullOrWhiteSpace(text)) return;
                        c.ReceivedId = id;
                        ChatReceived?.Invoke(new(id, peer.Name, text, false, peer.Id));
                    }
                    SendChatSecure(peer, c, TypeChatAck, payload[..4]);
                }
            }
        }
    }

    void CompleteChat(uint peerId, ChatChannel c, bool delivered)
    {
        c.Pending = null;
        ChatDelivery?.Invoke(peerId, c.PendingId, delivered);
    }

    void TickChat(long now)
    {
        foreach (var (id, c) in chats.ToArray())
        {
            if (c.Pending is null) continue;
            if (now - c.Started >= 10000 || !peers.TryGetValue(id, out var peer)) RemoveChat(id);
            else if (now - c.LastSent >= 500) SendPendingChat(peer, c, now);
        }
    }

    void RemoveChat(uint id)
    {
        if (!chats.Remove(id, out var c)) return;
        if (c.Pending is not null) CompleteChat(id, c, false);
        c.Ephemeral?.Dispose();
        c.Session?.Dispose();
    }
}
