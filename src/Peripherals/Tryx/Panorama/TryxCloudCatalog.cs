using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Tryx.Panorama.Crypto;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>One catalog entry: the fields the panel install flow needs to show
/// a pick list and then fetch + decrypt the asset.</summary>
public sealed class TryxCloudMaterial
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string InstallFileName { get; init; } = "";
}

/// <summary>
/// Client for Kanali's Tryx cloud wallpaper catalog (<c>kanali2-api.tryxzone.com</c>).
///
/// Wire format, decoded from the desktop app's axios wrapper (`mr`/`lM`/`Mf` in
/// the Kanali Electron bundle): every request and response body is an SM2
/// ciphertext (GM/T 0003.4, mode C1C3C2 / Rv=1) over the JSON-encoded payload,
/// and that ciphertext hex is itself carried as a bare JSON string scalar - not
/// `{data:"..."}`. `mr` builds the request with `Content-Type:
/// application/json` and passes axios the raw hex STRING; axios's default
/// transformRequest still runs `JSON.stringify` on it because the
/// Content-Type-is-json check short-circuits its is-this-a-plain-object check,
/// so the literal wire body is a JSON string scalar (`"04a1b2..."`, quotes
/// included). The response mirrors this: the server answers a JSON string
/// scalar, and axios's default `responseType:"json"` unwraps it back to a bare
/// string before the SM2 decrypt (`Mf`) runs on it - so both directions here
/// serialize/deserialize the ciphertext through the plain `string`
/// JsonTypeInfo, not a wrapper object.
///
/// `lM` (encrypt) prepends a literal "04" byte the underlying SM2 library
/// doesn't add on its own; <see cref="Sm2.Encrypt"/> mirrors the library and
/// leaves that prefix out, so this client adds it explicitly to match `lM`
/// exactly. The embedded public key (encrypts client to server) and private
/// key (decrypts server to client) are independently provisioned - they are
/// not a mathematical keypair, so do not expect one to derive the other.
///
/// UNVERIFIED: the exact response envelope field names (code/data/errorMsg)
/// and the SM4 mode used to encrypt the downloadable assets (assumed ECB with
/// PKCS7 padding, the most streamable option and the default most published
/// SM4 references use when a mode isn't stated) are read from static
/// analysis only - no live call against this API has been made. Confirm
/// against a real response before shipping.
/// </summary>
public sealed class TryxCloudCatalog
{
    private const string BaseUrl = "https://kanali2-api.tryxzone.com/api";

    /// <summary>Encrypts every outgoing request body. Server-held; only the
    /// server can decrypt what this key encrypts.</summary>
    private const string RequestPublicKeyHex =
        "04e4da9b393d64baff3294f9d57c2939596dfee21e0388b3c7688b9e066d28ce907dc933f45d7694c3b0beded76ae48a9295e49054fca75594d99a698a3c3ea294";

    /// <summary>Decrypts every incoming response body. Paired with a public key
    /// the server holds for this client, not with <see cref="RequestPublicKeyHex"/>.</summary>
    private const string ResponsePrivateKeyHex =
        "405456c4e0f493d31beb0af38a6f248b5a522d1ae0835589e905aec07efcf54a";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    { Timeout = TimeSpan.FromMinutes(2) };

    // Signed CDN cover URLs from the last catalog fetch, keyed by material id, so the
    // cover-proxy route can stream one without re-querying the API.
    private static readonly ConcurrentDictionary<int, string> CoverUrls = new();

    /// <summary>Query the catalog for a product code (Panorama = "PANO_1011"),
    /// then resolve the returned ids to full records.</summary>
    public async Task<List<TryxCloudMaterial>> GetCatalogAsync(string code, CancellationToken ct)
    {
        var queryJson = JsonSerializer.Serialize(
            new TryxMaterialQueryRequest { Code = code }, TryxCloudJsonContext.Default.TryxMaterialQueryRequest);
        var ids = await SendAsync(
            "/app-material/query", queryJson, TryxCloudJsonContext.Default.TryxCloudEnvelopeListInt32, ct)
            .ConfigureAwait(false);
        if (ids is null || ids.Count == 0)
        {
            return new List<TryxCloudMaterial>();
        }

        var getJson = JsonSerializer.Serialize(
            new TryxMaterialGetRequest { IdList = ids }, TryxCloudJsonContext.Default.TryxMaterialGetRequest);
        var records = await SendAsync(
            "/app-material/get", getJson, TryxCloudJsonContext.Default.TryxCloudEnvelopeListTryxMaterialRecord, ct)
            .ConfigureAwait(false);
        if (records is null)
        {
            return new List<TryxCloudMaterial>();
        }

        foreach (var r in records) CoverUrls[r.Id] = r.CoverFileUrl;
        return records.Select(r => new TryxCloudMaterial
        {
            Id = r.Id,
            Name = r.Name,
            CoverUrl = r.CoverFileUrl,
            InstallFileName = InstallFileName(r.Id),
        }).ToList();
    }

    /// <summary>Opens the CDN cover image for a material, or null if unknown/unreachable.
    /// Caller owns the returned stream. On a cold cache (fresh service start) the catalog is
    /// fetched first so a cover request that arrives before any catalog load still resolves.</summary>
    public async Task<Stream?> OpenCoverAsync(int id, CancellationToken ct)
    {
        if (!CoverUrls.TryGetValue(id, out var url))
        {
            try { await GetCatalogAsync("PANO_1011", ct).ConfigureAwait(false); }
            catch { /* offline; nothing to serve */ }
            if (!CoverUrls.TryGetValue(id, out url)) return null;
        }
        var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            resp.Dispose();
            return null;
        }
        return await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> GetDownloadUrlAsync(int id, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            new TryxMaterialGetUrlRequest { Id = id }, TryxCloudJsonContext.Default.TryxMaterialGetUrlRequest);
        var url = await SendAsync("/app-material/getUrl", json, TryxCloudJsonContext.Default.TryxCloudEnvelopeString, ct)
            .ConfigureAwait(false);
        return url ?? throw new InvalidOperationException("Tryx cloud API returned no download URL.");
    }

    public async Task<byte[]> GetPlainSm4KeyAsync(CancellationToken ct)
    {
        var hex = await SendAsync("/app-sm/get-sm4-key", "{}", TryxCloudJsonContext.Default.TryxCloudEnvelopeString, ct)
            .ConfigureAwait(false);
        return Convert.FromHexString(hex ?? throw new InvalidOperationException("Tryx cloud API returned no SM4 key."));
    }

    /// <summary>Downloads the SM4-encrypted asset and decrypts it to <paramref name="destPath"/>
    /// as a raw H.264 Annex-B stream. Returns the install file name the panel protocol expects.
    /// Wire format (decoded live): the asset is a 16-byte IV followed by SM4-CBC ciphertext of
    /// the H.264, keyed by /app-sm/get-sm4-key; PKCS7-padded.</summary>
    public async Task<string> DownloadAndDecryptAsync(int id, string destPath, CancellationToken ct)
    {
        var url = await GetDownloadUrlAsync(id, ct).ConfigureAwait(false);
        var key = await GetPlainSm4KeyAsync(ct).ConfigureAwait(false);

        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var encrypted = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (encrypted.Length <= 16)
        {
            throw new InvalidOperationException("Tryx cloud asset too small to contain an IV.");
        }

        var iv = encrypted[..16];
        var body = encrypted[16..];
        var plain = Sm4.DecryptCbc(key, iv, body);
        await File.WriteAllBytesAsync(destPath, plain, ct).ConfigureAwait(false);

        return InstallFileName(id);
    }

    private static string InstallFileName(int id) => $"download_{id}.mp4.h264_2240x1080";

    // ── SM2-wrapped request/response envelope ───────────────────────────────

    private static async Task<TOut?> SendAsync<TOut>(
        string path, string plaintextJson, JsonTypeInfo<TryxCloudEnvelope<TOut>> envelopeInfo, CancellationToken ct)
    {
        // The server's SM2 gateway wants the ciphertext hex as the RAW request body, not a
        // JSON-quoted string (Kanali's bundled axios sends the string as-is; a quoted body is
        // rejected with a 500). The 200 response is likewise raw hex, not a JSON scalar.
        var cipherHex = "04" + Sm2.Encrypt(plaintextJson, RequestPublicKeyHex);

        using var content = new StringContent(cipherHex, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(BaseUrl + path, content, ct).ConfigureAwait(false);
        var responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var bodyHead = responseBody.Length > 300 ? responseBody[..300] : responseBody;
            Platform.ServiceLog.Warn($"[tryx] cloud {path} -> {(int)resp.StatusCode}; body={bodyHead}");
            resp.EnsureSuccessStatusCode();
        }
        // Tolerate a quoted or unquoted hex response.
        var responseHex = responseBody.Trim().Trim('"');
        if (string.IsNullOrEmpty(responseHex))
        {
            throw new InvalidOperationException($"Tryx cloud API returned an empty response for {path}.");
        }

        var decryptedJson = Sm2.Decrypt(responseHex, ResponsePrivateKeyHex);
        var envelope = JsonSerializer.Deserialize(decryptedJson, envelopeInfo)
            ?? throw new InvalidOperationException($"Tryx cloud API returned an unparsable envelope for {path}.");

        if (envelope.Code != 200 && envelope.Code != 201)
        {
            throw new InvalidOperationException($"Tryx cloud API error on {path}: code={envelope.Code} {envelope.ErrorMsg}");
        }

        return envelope.Data;
    }
}

// ── Wire DTOs (SM2-encrypted plaintext bodies, GM/T-decrypted response bodies) ──

public sealed class TryxMaterialQueryRequest
{
    [JsonPropertyName("code")] public string Code { get; init; } = "";
}

public sealed class TryxMaterialGetRequest
{
    [JsonPropertyName("idList")] public List<int> IdList { get; init; } = new();
}

public sealed class TryxMaterialGetUrlRequest
{
    [JsonPropertyName("id")] public int Id { get; init; }
}

public sealed class TryxMaterialRecord
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("coverFileUrl")] public string CoverFileUrl { get; set; } = "";
    [JsonPropertyName("previewFileUrl")] public string PreviewFileUrl { get; set; } = "";
    [JsonPropertyName("hardwareInfo")] public List<TryxHardwareInfoEntry> HardwareInfo { get; set; } = new();
}

public sealed class TryxHardwareInfoEntry
{
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
}

public sealed class TryxCloudEnvelope<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("errorMsg")] public string? ErrorMsg { get; set; }
}
