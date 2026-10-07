using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace WolfSpeak;

/// <summary>
/// This install's long-term signing key (P-256), stored DPAPI-protected for the current Windows user.
/// It signs the call handshake, so a friend's PC recognises you by its fingerprint, not by your name.
/// </summary>
public sealed class Identity
{
    static readonly byte[] Entropy = "WolfSpeak identity v1"u8.ToArray();
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WolfSpeak", "identity.key");

    readonly ECDsa key;

    /// <summary>SubjectPublicKeyInfo of the signing key.</summary>
    public byte[] PublicKey { get; }

    Identity(ECDsa key)
    {
        this.key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
    }

    public static Identity LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
                var loaded = ECDsa.Create();
                loaded.ImportPkcs8PrivateKey(pkcs8, out _);
                CryptographicOperations.ZeroMemory(pkcs8);
                if (CallCrypto.IsP256(loaded)) return new Identity(loaded);
            }
        }
        catch (Exception ex) { Log.Write("Identity key unreadable, creating a new one", ex); }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var pkcs8 = created.ExportPkcs8PrivateKey();
            File.WriteAllBytes(FilePath, ProtectedData.Protect(pkcs8, Entropy, DataProtectionScope.CurrentUser));
            CryptographicOperations.ZeroMemory(pkcs8);
        }
        catch (Exception ex) { Log.Write("Could not save identity key", ex); }
        return new Identity(created);
    }

    public byte[] Sign(ReadOnlySpan<byte> data) => key.SignData(data, HashAlgorithmName.SHA256);
}

/// <summary>
/// Call handshake + packet protection. Each call gets fresh ephemeral ECDH keys signed by both
/// identities; the derived AES-256-GCM key encrypts and authenticates every in-call packet.
/// </summary>
public static class CallCrypto
{
    public const int CounterSize = 8, TagSize = 16, Overhead = CounterSize + TagSize;
    const string P256Oid = "1.2.840.10045.3.1.7";

    static readonly byte[] RequestLabel = "WolfSpeak call request v1"u8.ToArray();
    static readonly byte[] AcceptLabel = "WolfSpeak call accept v1"u8.ToArray();
    static readonly byte[] KeyInfo = "WolfSpeak call key v1"u8.ToArray();

    public static bool IsP256(AsymmetricAlgorithm key) =>
        key switch
        {
            ECDsa e => e.ExportParameters(false).Curve.Oid?.Value == P256Oid,
            ECDiffieHellman d => d.ExportParameters(false).Curve.Oid?.Value == P256Oid,
            _ => false,
        };

    /// <summary>Short, human-comparable fingerprint of an identity public key.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> identityKey) =>
        Convert.ToHexString(SHA256.HashData(identityKey).AsSpan(0, 8));

    public static ECDiffieHellman NewEphemeral() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    // Handshake payload: ephLen(1) eph | idLen(1) id | signature (rest)
    // Request signs: label | senderId | eph_caller
    // Accept  signs: label | senderId | eph_callee | eph_caller

    public static byte[] BuildRequest(Identity me, uint myId, byte[] myEph) =>
        BuildHandshake(me, myEph, Signed(RequestLabel, myId, myEph, []));

    public static byte[] BuildAccept(Identity me, uint myId, byte[] myEph, byte[] callerEph) =>
        BuildHandshake(me, myEph, Signed(AcceptLabel, myId, myEph, callerEph));

    /// <summary>Checks a call request; returns the caller's ephemeral key and identity, or false if forged/garbled.</summary>
    public static bool VerifyRequest(ReadOnlySpan<byte> payload, uint senderId, out byte[] eph, out byte[] identity) =>
        Verify(payload, out eph, out identity, e => Signed(RequestLabel, senderId, e, []));

    /// <summary>Checks a call accept that must answer <paramref name="myEph"/>.</summary>
    public static bool VerifyAccept(ReadOnlySpan<byte> payload, uint senderId, byte[] myEph, out byte[] eph, out byte[] identity) =>
        Verify(payload, out eph, out identity, e => Signed(AcceptLabel, senderId, e, myEph));

    static byte[] BuildHandshake(Identity me, byte[] eph, byte[] toSign)
    {
        var sig = me.Sign(toSign);
        var id = me.PublicKey;
        var pkt = new byte[2 + eph.Length + id.Length + sig.Length];
        pkt[0] = (byte)eph.Length;
        eph.CopyTo(pkt, 1);
        pkt[1 + eph.Length] = (byte)id.Length;
        id.CopyTo(pkt, 2 + eph.Length);
        sig.CopyTo(pkt, 2 + eph.Length + id.Length);
        return pkt;
    }

    static bool Verify(ReadOnlySpan<byte> payload, out byte[] eph, out byte[] identity, Func<byte[], byte[]> signedFor)
    {
        eph = identity = [];
        try
        {
            if (payload.Length < 1) return false;
            int ephLen = payload[0];
            if (payload.Length < 2 + ephLen) return false;
            int idLen = payload[1 + ephLen];
            if (payload.Length <= 2 + ephLen + idLen) return false;
            eph = payload.Slice(1, ephLen).ToArray();
            identity = payload.Slice(2 + ephLen, idLen).ToArray();
            var sig = payload[(2 + ephLen + idLen)..];

            using var ephKey = ECDiffieHellman.Create();
            ephKey.ImportSubjectPublicKeyInfo(eph, out int read);
            if (read != eph.Length || !IsP256(ephKey)) return false;

            using var idKey = ECDsa.Create();
            idKey.ImportSubjectPublicKeyInfo(identity, out read);
            if (read != identity.Length || !IsP256(idKey)) return false;

            return idKey.VerifyData(signedFor(eph), sig, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
    }

    static byte[] Signed(byte[] label, uint senderId, byte[] eph, byte[] otherEph)
    {
        var data = new byte[label.Length + 4 + eph.Length + otherEph.Length];
        label.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(label.Length), senderId);
        eph.CopyTo(data, label.Length + 4);
        otherEph.CopyTo(data, label.Length + 4 + eph.Length);
        return data;
    }

    /// <summary>AES-256 key for the call: ECDH(ephemerals) → HKDF-SHA256, salted with both ephemerals.</summary>
    public static byte[] DeriveKey(ECDiffieHellman mine, byte[] theirEph, byte[] callerEph, byte[] calleeEph)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(theirEph, out _);
        var secret = mine.DeriveRawSecretAgreement(other.PublicKey);
        try
        {
            var salt = new byte[callerEph.Length + calleeEph.Length];
            callerEph.CopyTo(salt, 0);
            calleeEph.CopyTo(salt, callerEph.Length);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, KeyInfo);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}

/// <summary>
/// AES-256-GCM for one call. Packet: header(7) | counter(8) | ciphertext | tag(16); the header and
/// counter are authenticated too. Each direction has its own nonce space and the receiver only accepts
/// increasing counters, so packets can't be forged, altered or replayed.
/// </summary>
public sealed class CallSession
{
    readonly AesGcm sealer, opener; // separate instances: sending and receiving run on different threads
    readonly object sendLock = new();
    readonly byte sendDirection, receiveDirection;
    ulong sendCounter, lastReceived;
    bool receivedAny;

    public CallSession(byte[] key, bool isCaller)
    {
        sealer = new AesGcm(key, CallCrypto.TagSize);
        opener = new AesGcm(key, CallCrypto.TagSize);
        CryptographicOperations.ZeroMemory(key);
        sendDirection = isCaller ? (byte)0 : (byte)1;
        receiveDirection = (byte)(1 - sendDirection);
    }

    /// <summary>Encrypts <paramref name="plain"/> into <paramref name="packet"/> after its header. Returns the packet length.</summary>
    public int Seal(Span<byte> packet, int headerSize, ReadOnlySpan<byte> plain)
    {
        Span<byte> nonce = stackalloc byte[12];
        lock (sendLock)
        {
            ulong counter = ++sendCounter;
            BinaryPrimitives.WriteUInt64LittleEndian(packet.Slice(headerSize), counter);
            MakeNonce(nonce, sendDirection, counter);
            int ctStart = headerSize + CallCrypto.CounterSize;
            sealer.Encrypt(nonce, plain, packet.Slice(ctStart, plain.Length),
                packet.Slice(ctStart + plain.Length, CallCrypto.TagSize), packet[..ctStart]);
            return ctStart + plain.Length + CallCrypto.TagSize;
        }
    }

    /// <summary>Decrypts and authenticates; false for forged, corrupted or replayed packets. Receive thread only.</summary>
    public bool TryOpen(ReadOnlySpan<byte> packet, int headerSize, Span<byte> plain, out int length)
    {
        length = packet.Length - headerSize - CallCrypto.Overhead;
        if (length < 0 || length > plain.Length) return false;
        ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(headerSize));
        if (receivedAny && counter <= lastReceived) return false;

        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(nonce, receiveDirection, counter);
        int ctStart = headerSize + CallCrypto.CounterSize;
        try
        {
            opener.Decrypt(nonce, packet.Slice(ctStart, length), packet.Slice(ctStart + length, CallCrypto.TagSize),
                plain[..length], packet[..ctStart]);
        }
        catch (AuthenticationTagMismatchException) { return false; }

        lastReceived = counter;
        receivedAny = true;
        return true;
    }

    static void MakeNonce(Span<byte> nonce, byte direction, ulong counter)
    {
        nonce.Clear();
        nonce[0] = direction;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }
}
