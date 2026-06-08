using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Smart.Drivers.Hue;

/// <summary>
/// Minimal DTLS 1.2 client for the single ciphersuite the Hue bridge accepts:
/// <c>TLS_PSK_WITH_AES_128_GCM_SHA256</c>. No certificates, no negotiation — a
/// fixed PSK handshake + an AES-128-GCM record layer. Hand-rolled (vs pulling
/// BouncyCastle) so the whole thing stays NativeAOT-clean: it uses only BCL
/// primitives (<see cref="AesGcm"/>, <see cref="HMACSHA256"/>,
/// <see cref="RandomNumberGenerator"/>) that the AOT compiler fully supports.
///
/// Scope: client side only, no renegotiation, best-effort retransmission for a
/// LAN bridge. Intended for the Hue Entertainment stream (UDP 2100).
///
/// SINGLE-USE: each instance generates a fresh client random and derives fresh
/// keys, so the (key, GCM nonce) pair is never reused. Never reconnect or reuse
/// an instance — always create a new one per session (the record sequence only
/// guarantees nonce uniqueness within one instance's epoch).
/// </summary>
public sealed class DtlsPskClient : IDisposable
{
    private static readonly byte[] Dtls12 = { 0xFE, 0xFD };
    private static readonly byte[] CipherSuite = { 0x00, 0xA8 }; // TLS_PSK_WITH_AES_128_GCM_SHA256

    private const byte CtChangeCipherSpec = 20;
    private const byte CtAlert = 21;
    private const byte CtHandshake = 22;
    private const byte CtAppData = 23;

    private const byte HtClientHello = 1;
    private const byte HtServerHello = 2;
    private const byte HtHelloVerifyRequest = 3;
    private const byte HtServerKeyExchange = 12;
    private const byte HtServerHelloDone = 14;
    private const byte HtClientKeyExchange = 16;
    private const byte HtFinished = 20;

    private readonly Socket _sock;
    private readonly byte[] _identity;
    private readonly byte[] _psk;

    private byte[] _clientRandom = Array.Empty<byte>();
    private byte[] _serverRandom = Array.Empty<byte>();
    private byte[] _cookie = Array.Empty<byte>();
    private byte[] _masterSecret = Array.Empty<byte>();
    private AesGcm? _clientGcm;
    private AesGcm? _serverGcm;
    private byte[] _clientIv = Array.Empty<byte>();
    private byte[] _serverIv = Array.Empty<byte>();

    private ushort _sendEpoch;
    private ulong _sendSeq;       // per-epoch record sequence
    private ushort _msgSeq;       // handshake message_seq for our messages
    private readonly IncrementalHash _transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _rxBuf = new byte[4096];

    private DtlsPskClient(Socket sock, byte[] identity, byte[] psk)
    {
        _sock = sock;
        _identity = identity;
        _psk = psk;
    }

    /// <summary>Open a UDP socket to the bridge and run the full PSK handshake.
    /// Returns a connected client ready for <see cref="SendAsync"/>, or throws.</summary>
    public static async Task<DtlsPskClient> ConnectAsync(string host, int port, string pskIdentity, byte[] psk, CancellationToken ct)
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.Connect(IPAddress.Parse(host), port);
        var client = new DtlsPskClient(sock, Encoding.ASCII.GetBytes(pskIdentity), psk);
        try { await client.HandshakeAsync(ct).ConfigureAwait(false); }
        catch { client.Dispose(); throw; }
        return client;
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        _clientRandom = RandomNumberGenerator.GetBytes(32);

        // Flight 1: ClientHello (no cookie) → expect HelloVerifyRequest.
        await SendFlightUntilAsync(() => SendPlaintextHandshake(HtClientHello, BuildClientHello(), addToTranscript: false),
            HtHelloVerifyRequest, ct).ConfigureAwait(false);

        // Flight 3: ClientHello (with cookie) → expect ServerHelloDone.
        // The 2nd ClientHello onward is part of the Finished transcript.
        await SendFlightUntilAsync(() => SendPlaintextHandshake(HtClientHello, BuildClientHello(), addToTranscript: true),
            HtServerHelloDone, ct).ConfigureAwait(false);

        DeriveKeys();

        // Flight 5: ClientKeyExchange + ChangeCipherSpec + (encrypted) Finished → expect server Finished.
        await SendFlightUntilAsync(SendClientFinishFlight, HtFinished, ct).ConfigureAwait(false);
    }

    // ── Flight driver ────────────────────────────────────────────────────────

    private bool _sawServerHelloDone;
    private bool _sawServerFinished;

    // Sends a flight ONCE then waits. Retransmitting in place would re-increment
    // message_seq and re-append to the transcript, corrupting the handshake — so
    // on loss the caller retries the whole handshake (a fresh client) instead.
    private async Task SendFlightUntilAsync(Action sendFlight, byte expectedType, CancellationToken ct)
    {
        _sawServerHelloDone = false;
        _sawServerFinished = false;
        sendFlight();

        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            var n = await ReceiveAsync(800, ct).ConfigureAwait(false);
            if (n <= 0) continue;
            ProcessDatagram(_rxBuf.AsSpan(0, n));
            if (expectedType == HtHelloVerifyRequest && _cookie.Length > 0) return;
            if (expectedType == HtServerHelloDone && _sawServerHelloDone) return;
            if (expectedType == HtFinished && _sawServerFinished) return;
        }
        throw new TimeoutException($"DTLS handshake stalled waiting for message type {expectedType}");
    }

    private async Task<int> ReceiveAsync(int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { return await _sock.ReceiveAsync(_rxBuf, SocketFlags.None, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return 0; }
        catch (SocketException) { return 0; }
    }

    // ── Datagram / record parsing ──────────────────────────────────────────────

    private void ProcessDatagram(ReadOnlySpan<byte> dg)
    {
        var off = 0;
        while (off + 13 <= dg.Length)
        {
            var type = dg[off];
            var epoch = (ushort)((dg[off + 3] << 8) | dg[off + 4]);
            var len = (dg[off + 11] << 8) | dg[off + 12];
            var fragStart = off + 13;
            if (fragStart + len > dg.Length) break;
            var frag = dg.Slice(fragStart, len);

            if (type == CtAlert)
            {
                var level = frag.Length > 0 ? frag[0] : 0;
                var desc = frag.Length > 1 ? frag[1] : 0;
                throw new IOException($"DTLS alert level={level} desc={desc}");
            }
            else if (type == CtChangeCipherSpec)
            {
                // server CCS — next server records (epoch 1) are encrypted
            }
            else if (type == CtHandshake)
            {
                if (epoch == 0) ProcessPlaintextHandshakeRecord(frag);
                else ProcessEncryptedHandshakeRecord(dg.Slice(off, 13), frag);
            }
            off = fragStart + len;
        }
    }

    private void ProcessPlaintextHandshakeRecord(ReadOnlySpan<byte> frag)
    {
        var off = 0;
        while (off + 12 <= frag.Length)
        {
            var htype = frag[off];
            var hlen = (frag[off + 1] << 16) | (frag[off + 2] << 8) | frag[off + 3];
            var bodyStart = off + 12;
            if (bodyStart + hlen > frag.Length) break;
            var body = frag.Slice(bodyStart, hlen);
            HandleHandshakeMessage(htype, frag.Slice(off, 12 + hlen), body, addToTranscript: htype != HtHelloVerifyRequest);
            off = bodyStart + hlen;
        }
    }

    private void HandleHandshakeMessage(byte htype, ReadOnlySpan<byte> full, ReadOnlySpan<byte> body, bool addToTranscript)
    {
        if (addToTranscript) _transcript.AppendData(full);

        switch (htype)
        {
            case HtHelloVerifyRequest:
                // body: server_version(2) + cookie_len(1) + cookie
                if (body.Length >= 3)
                {
                    var clen = body[2];
                    _cookie = body.Slice(3, Math.Min(clen, body.Length - 3)).ToArray();
                }
                break;
            case HtServerHello:
                // body: version(2) + random(32) + sid_len(1) + sid + cipher(2) + comp(1) + ext...
                if (body.Length >= 34) _serverRandom = body.Slice(2, 32).ToArray();
                break;
            case HtServerKeyExchange:
                // PSK identity hint — ignored.
                break;
            case HtServerHelloDone:
                _sawServerHelloDone = true;
                break;
        }
    }

    private void ProcessEncryptedHandshakeRecord(ReadOnlySpan<byte> header, ReadOnlySpan<byte> frag)
    {
        // Server Finished (epoch 1). Decrypt; we don't strictly verify verify_data.
        var plain = DecryptRecord(header, frag);
        if (plain is null) return;
        // plaintext is a handshake message (Finished). Mark complete.
        if (plain.Length >= 1 && plain[0] == HtFinished) _sawServerFinished = true;
    }

    // ── Outgoing flights ───────────────────────────────────────────────────────

    private void SendClientFinishFlight()
    {
        // ClientKeyExchange (psk_identity)
        var cke = new List<byte>();
        WriteU16(cke, (ushort)_identity.Length);
        cke.AddRange(_identity);
        SendPlaintextHandshake(HtClientKeyExchange, cke.ToArray(), addToTranscript: true);

        // ChangeCipherSpec (epoch 0), then switch to epoch 1 for the encrypted Finished.
        SendRecord(CtChangeCipherSpec, new byte[] { 0x01 });
        _sendEpoch = 1;
        _sendSeq = 0;

        // verify_data = PRF(master, "client finished", SHA256(transcript))[0..12].
        // The transcript IS a running SHA256, so GetHashAndReset() yields the digest.
        // Server Finished verification is skipped, so resetting the transcript is fine.
        var transcriptHash = _transcript.GetHashAndReset();
        var verify = Prf(_masterSecret, "client finished", transcriptHash, 12);
        SendEncryptedHandshake(BuildHandshake(HtFinished, verify));
    }

    private byte[] BuildClientHello()
    {
        var b = new List<byte>(64);
        b.AddRange(Dtls12);                 // client_version
        b.AddRange(_clientRandom);          // random[32]
        b.Add(0x00);                        // session_id length
        b.Add((byte)_cookie.Length);        // cookie length
        b.AddRange(_cookie);                // cookie
        WriteU16(b, (ushort)CipherSuite.Length);
        b.AddRange(CipherSuite);
        b.Add(0x01);                        // compression methods length
        b.Add(0x00);                        // null compression
        WriteU16(b, 0);                     // extensions length = 0
        return b.ToArray();
    }

    // ── Record / handshake framing ─────────────────────────────────────────────

    private void SendPlaintextHandshake(byte htype, byte[] body, bool addToTranscript)
    {
        var full = BuildHandshake(htype, body);
        if (addToTranscript) _transcript.AppendData(full);
        SendRecord(CtHandshake, full);
    }

    /// <summary>Build a reassembled DTLS handshake message: 12-byte header (type,
    /// 24-bit length, message_seq, frag_offset=0, frag_length=length) + body.</summary>
    private byte[] BuildHandshake(byte htype, byte[] body)
    {
        var msg = new byte[12 + body.Length];
        msg[0] = htype;
        WriteU24(msg, 1, body.Length);
        msg[4] = (byte)(_msgSeq >> 8); msg[5] = (byte)_msgSeq;
        WriteU24(msg, 6, 0);            // fragment_offset
        WriteU24(msg, 9, body.Length);  // fragment_length
        Array.Copy(body, 0, msg, 12, body.Length);
        _msgSeq++;
        return msg;
    }

    private void SendEncryptedHandshake(byte[] handshakeMsg)
    {
        var fragment = EncryptRecord(CtHandshake, handshakeMsg);
        SendRecordRaw(CtHandshake, fragment);
    }

    private void SendRecord(byte type, byte[] payload)
    {
        if (_sendEpoch == 0) { SendRecordRaw(type, payload); return; }
        SendRecordRaw(type, EncryptRecord(type, payload));
    }

    private void SendRecordRaw(byte type, byte[] fragment)
    {
        var seq = _sendSeq++;
        var rec = new byte[13 + fragment.Length];
        rec[0] = type;
        rec[1] = Dtls12[0]; rec[2] = Dtls12[1];
        rec[3] = (byte)(_sendEpoch >> 8); rec[4] = (byte)_sendEpoch;
        WriteU48(rec, 5, seq);
        rec[11] = (byte)(fragment.Length >> 8); rec[12] = (byte)fragment.Length;
        Array.Copy(fragment, 0, rec, 13, fragment.Length);
        _sock.Send(rec);
    }

    // ── AES-128-GCM record protection ──────────────────────────────────────────

    private byte[] EncryptRecord(byte type, byte[] plaintext)
    {
        // explicit nonce = epoch(2)||seq(6) of THIS record.
        var seq = _sendSeq; // SendRecordRaw will use the same value next
        Span<byte> explicitNonce = stackalloc byte[8];
        explicitNonce[0] = (byte)(_sendEpoch >> 8); explicitNonce[1] = (byte)_sendEpoch;
        WriteU48Span(explicitNonce.Slice(2), seq);

        Span<byte> nonce = stackalloc byte[12];
        _clientIv.AsSpan().CopyTo(nonce);
        explicitNonce.CopyTo(nonce.Slice(4));

        var aad = BuildAad(explicitNonce, type, plaintext.Length);
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        _clientGcm!.Encrypt(nonce, plaintext, ct, tag, aad);

        var fragment = new byte[8 + ct.Length + 16];
        explicitNonce.CopyTo(fragment);
        Array.Copy(ct, 0, fragment, 8, ct.Length);
        Array.Copy(tag, 0, fragment, 8 + ct.Length, 16);
        return fragment;
    }

    private byte[]? DecryptRecord(ReadOnlySpan<byte> header, ReadOnlySpan<byte> fragment)
    {
        if (fragment.Length < 8 + 16) return null;
        Span<byte> explicitNonce = stackalloc byte[8];
        fragment.Slice(0, 8).CopyTo(explicitNonce);
        var ctLen = fragment.Length - 8 - 16;

        Span<byte> nonce = stackalloc byte[12];
        _serverIv.AsSpan().CopyTo(nonce);
        explicitNonce.CopyTo(nonce.Slice(4));

        var aad = BuildAad(explicitNonce, header[0], ctLen);
        var plain = new byte[ctLen];
        try
        {
            _serverGcm!.Decrypt(nonce, fragment.Slice(8, ctLen), fragment.Slice(8 + ctLen, 16), plain, aad);
            return plain;
        }
        catch { return null; }
    }

    // additional_data = seq_num(8: epoch||seq) || type(1) || version(2) || length(2)
    private static byte[] BuildAad(ReadOnlySpan<byte> seqNum8, byte type, int len)
    {
        var aad = new byte[13];
        seqNum8.CopyTo(aad);
        aad[8] = type;
        aad[9] = Dtls12[0]; aad[10] = Dtls12[1];
        aad[11] = (byte)(len >> 8); aad[12] = (byte)len;
        return aad;
    }

    // ── Application data ───────────────────────────────────────────────────────

    /// <summary>Send one application-data record (the HueStream packet). Fire-and-forget
    /// over UDP — no ack. Safe to call at the stream rate.</summary>
    public void Send(ReadOnlySpan<byte> payload)
    {
        SendRecordRaw(CtAppData, EncryptRecord(CtAppData, payload.ToArray()));
    }

    public Task SendAsync(byte[] payload, CancellationToken ct)
    {
        Send(payload);
        return Task.CompletedTask;
    }

    // ── Key schedule ─────────────────────────────────────────────────────────────

    private void DeriveKeys()
    {
        var premaster = PskPremaster(_psk);
        _masterSecret = Prf(premaster, "master secret", Concat(_clientRandom, _serverRandom), 48);
        var keyBlock = Prf(_masterSecret, "key expansion", Concat(_serverRandom, _clientRandom), 40);
        var clientKey = keyBlock.AsSpan(0, 16).ToArray();
        var serverKey = keyBlock.AsSpan(16, 16).ToArray();
        _clientIv = keyBlock.AsSpan(32, 4).ToArray();
        _serverIv = keyBlock.AsSpan(36, 4).ToArray();
        _clientGcm = new AesGcm(clientKey, 16);
        _serverGcm = new AesGcm(serverKey, 16);
    }

    /// <summary>RFC 4279 PSK premaster secret: uint16(N)||zeros(N)||uint16(N)||psk, N=psk length.</summary>
    public static byte[] PskPremaster(byte[] psk)
    {
        var n = psk.Length;
        var ms = new byte[2 + n + 2 + n];
        ms[0] = (byte)(n >> 8); ms[1] = (byte)n;          // other_secret length
        // other_secret bytes are zero (already)
        var o = 2 + n;
        ms[o] = (byte)(n >> 8); ms[o + 1] = (byte)n;      // psk length
        Array.Copy(psk, 0, ms, o + 2, n);
        return ms;
    }

    /// <summary>TLS 1.2 PRF = P_SHA256(secret, label || seed).</summary>
    public static byte[] Prf(byte[] secret, string label, byte[] seed, int length)
    {
        var labelSeed = Concat(Encoding.ASCII.GetBytes(label), seed);
        var result = new byte[length];
        using var hmac = new HMACSHA256(secret);
        var a = hmac.ComputeHash(labelSeed); // A(1)
        var pos = 0;
        while (pos < length)
        {
            var output = hmac.ComputeHash(Concat(a, labelSeed));
            var copy = Math.Min(output.Length, length - pos);
            Array.Copy(output, 0, result, pos, copy);
            pos += copy;
            a = hmac.ComputeHash(a); // A(i+1)
        }
        return result;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Array.Copy(a, 0, r, 0, a.Length);
        Array.Copy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static void WriteU16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void WriteU24(byte[] b, int off, int v) { b[off] = (byte)(v >> 16); b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)v; }
    private static void WriteU48(byte[] b, int off, ulong v)
    {
        b[off] = (byte)(v >> 40); b[off + 1] = (byte)(v >> 32); b[off + 2] = (byte)(v >> 24);
        b[off + 3] = (byte)(v >> 16); b[off + 4] = (byte)(v >> 8); b[off + 5] = (byte)v;
    }
    private static void WriteU48Span(Span<byte> b, ulong v)
    {
        b[0] = (byte)(v >> 40); b[1] = (byte)(v >> 32); b[2] = (byte)(v >> 24);
        b[3] = (byte)(v >> 16); b[4] = (byte)(v >> 8); b[5] = (byte)v;
    }

    public void Dispose()
    {
        _clientGcm?.Dispose();
        _serverGcm?.Dispose();
        _transcript.Dispose();
        try { _sock.Dispose(); } catch { }
    }
}
