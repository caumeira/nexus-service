using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.StreamDeck;

public enum StreamDeckProtocolGeneration { Gen1, Gen2 }

public enum StreamDeckImageFormat { None, Bmp, Jpeg }

public enum StreamDeckRotation { Rot0, Rot90, Rot180, Rot270 }

public enum StreamDeckMirror { None, X, Y, Both }

/// <summary>
/// Per-model capability row: layout, key image shape, wire protocol
/// generation, and quirks. Verified=true means bench-confirmed (Mini only,
/// 2026-07-10); every other row is transcribed from the MIT references and
/// unproven on real hardware.
/// </summary>
public sealed class StreamDeckModel
{
    public string Name { get; }
    public int ProductId { get; }
    public int KeyCount { get; }
    public int Rows { get; }
    public int Columns { get; }
    public int KeyPixelSize { get; }
    public StreamDeckImageFormat ImageFormat { get; }
    public StreamDeckRotation Rotation { get; }
    public StreamDeckMirror Mirror { get; }
    public StreamDeckProtocolGeneration Protocol { get; }
    public bool Verified { get; }

    /// <summary>
    /// Gen1 image report length in bytes (report id + header + payload pages).
    /// 1024 for every gen1 model except the Original, whose firmware still
    /// uses the pre-1024 report size (elgato-streamdeck WriteImageParameters::for_key).
    /// </summary>
    public int ImageReportLength { get; }

    /// <summary>
    /// True only for the Original (0x0060): its firmware halves the image
    /// payload across exactly 2 pages instead of chunking at
    /// ImageReportLength - header, and numbers pages from 1 instead of 0
    /// (elgato-streamdeck lib.rs write_image_data_reports / send_image).
    /// </summary>
    public bool HalvedImagePayload { get; }

    /// <summary>Gen1 image page numbering base: 0 for every model except the Original (1).</summary>
    public int ImagePageNumberBase { get; }

    /// <summary>
    /// True only for the Original: both input-report key states and outgoing
    /// image key indices are addressed right-to-left within each row on the
    /// wire (python-elgato-streamdeck StreamDeckOriginal._convert_key_id_origin;
    /// elgato-streamdeck util::flip_key_index). Self-inverse, so the same
    /// remap converts canonical<->raw in both directions.
    /// </summary>
    public bool KeyIndexRightToLeft { get; }

    private StreamDeckModel(
        string name, int productId, int keyCount, int rows, int columns, int keyPixelSize,
        StreamDeckImageFormat imageFormat, StreamDeckRotation rotation, StreamDeckMirror mirror,
        StreamDeckProtocolGeneration protocol, bool verified,
        int imageReportLength = 1024, bool halvedImagePayload = false, int imagePageNumberBase = 0,
        bool keyIndexRightToLeft = false)
    {
        Name = name;
        ProductId = productId;
        KeyCount = keyCount;
        Rows = rows;
        Columns = columns;
        KeyPixelSize = keyPixelSize;
        ImageFormat = imageFormat;
        Rotation = rotation;
        Mirror = mirror;
        Protocol = protocol;
        Verified = verified;
        ImageReportLength = imageReportLength;
        HalvedImagePayload = halvedImagePayload;
        ImagePageNumberBase = imagePageNumberBase;
        KeyIndexRightToLeft = keyIndexRightToLeft;
    }

    /// <summary>
    /// Maps a canonical (user-facing) key index to the raw hardware index, or
    /// back again - the remap is its own inverse. Identity for every model
    /// except the Original.
    /// </summary>
    public int RemapKeyIndex(int index) =>
        KeyIndexRightToLeft ? StreamDeckModels.FlipWithinRow(index, Columns) : index;

    /// <summary>
    /// The /streamdeck/decks DTO's wire transform (nexus-web's
    /// deckKeyTransform.ts DeckKeyTransform: "none" | "flipBoth" |
    /// "mirrorXRot90"), the pixel transform a rendered key bitmap needs
    /// before it matches what this model expects on the wire.
    /// </summary>
    public string Transform =>
        ImageFormat == StreamDeckImageFormat.None
            ? "none"
            : Mirror == StreamDeckMirror.X && Rotation == StreamDeckRotation.Rot90
                ? "mirrorXRot90"
                : "flipBoth";

    internal static StreamDeckModel Gen1(
        string name, int productId, int keyCount, int rows, int columns, int keyPixelSize,
        bool verified,
        StreamDeckRotation rotation = StreamDeckRotation.Rot90, StreamDeckMirror mirror = StreamDeckMirror.X,
        bool keyIndexRightToLeft = false, int imageReportLength = 1024,
        bool halvedImagePayload = false, int imagePageNumberBase = 0) => new(
        name, productId, keyCount, rows, columns, keyPixelSize,
        StreamDeckImageFormat.Bmp, rotation, mirror,
        StreamDeckProtocolGeneration.Gen1, verified,
        imageReportLength, halvedImagePayload, imagePageNumberBase, keyIndexRightToLeft);

    internal static StreamDeckModel Gen2(
        string name, int productId, int keyCount, int rows, int columns, int keyPixelSize) => new(
        name, productId, keyCount, rows, columns, keyPixelSize,
        StreamDeckImageFormat.Jpeg, StreamDeckRotation.Rot0, StreamDeckMirror.Both,
        StreamDeckProtocolGeneration.Gen2, verified: false);

    internal static StreamDeckModel InputOnly(string name, int productId, int keyCount, int rows, int columns) => new(
        name, productId, keyCount, rows, columns, keyPixelSize: 0,
        StreamDeckImageFormat.None, StreamDeckRotation.Rot0, StreamDeckMirror.None,
        StreamDeckProtocolGeneration.Gen2, verified: false);
}

/// <summary>
/// The button-only Stream Deck capability table (plan streamdeck-support.md
/// §1), cross-verified against the MIT elgato-streamdeck crate
/// (src/info.rs, fetched 2026-07-10) and python-elgato-streamdeck
/// (StreamDeckMini.py / StreamDeckOriginal.py). Dials/touchscreen models
/// (Plus, Plus XL) are out of scope per the plan.
/// </summary>
public static class StreamDeckModels
{
    public const int VendorId = 0x0FD9;

    public static readonly IReadOnlyList<StreamDeckModel> All = new List<StreamDeckModel>
    {
        // Gen1: BMP, mirror-X + rot90 for the Mini family, halved-payload +
        // right-to-left remap for the legacy Original. Bare names (no
        // "Stream Deck " prefix) per plan streamdeck-support.md §1 - the
        // web's transformForModel fallback normalizes and matches against
        // these exact strings.
        StreamDeckModel.Gen1("Original", 0x0060, 15, 3, 5, 72,
            verified: false, rotation: StreamDeckRotation.Rot0, mirror: StreamDeckMirror.Both,
            keyIndexRightToLeft: true, imageReportLength: 8191, halvedImagePayload: true, imagePageNumberBase: 1),
        StreamDeckModel.Gen1("Mini", 0x0063, 6, 2, 3, 80, verified: true),
        StreamDeckModel.Gen1("Mini MK.2", 0x0090, 6, 2, 3, 80, verified: false),
        StreamDeckModel.Gen1("Mini Discord", 0x00b3, 6, 2, 3, 80, verified: false),
        StreamDeckModel.Gen1("Mini MK.2 Module", 0x00b8, 6, 2, 3, 80, verified: false),

        // Gen2: JPEG, flip-both.
        StreamDeckModel.Gen2("Original V2", 0x006d, 15, 3, 5, 72),
        StreamDeckModel.Gen2("MK.2", 0x0080, 15, 3, 5, 72),
        StreamDeckModel.Gen2("MK.2 Scissor", 0x00a5, 15, 3, 5, 72),
        StreamDeckModel.Gen2("MK.2 Module", 0x00b9, 15, 3, 5, 72),
        StreamDeckModel.Gen2("XL", 0x006c, 32, 4, 8, 96),
        StreamDeckModel.Gen2("XL V2", 0x008f, 32, 4, 8, 96),
        StreamDeckModel.Gen2("XL V2 Module", 0x00ba, 32, 4, 8, 96),
        // Neo's 8 LED keys use the standard gen2 key-image path. Its 2
        // capacitive touch keys report at input offsets KeyCount and
        // KeyCount+1 (documented in both MIT references), but KeyCount stays
        // 8 here (Rows*Columns) rather than 10 - wiring them as bindable
        // input needs the KeyCount/grid-layout invariant every other model
        // relies on to grow past Rows*Columns, which is out of this pass;
        // StreamDeckProtocol.DecodeGen2Input only decodes the first KeyCount
        // states. The 248x58 info screen stays out of v1 scope entirely.
        StreamDeckModel.Gen2("Neo", 0x009a, 8, 2, 4, 96),

        // Input-only: no key screens, buttons drive input dispatch only.
        StreamDeckModel.InputOnly("Pedal", 0x0086, 3, 1, 3),
    };

    public static StreamDeckModel? ByProductId(int productId) => All.FirstOrDefault(m => m.ProductId == productId);

    internal static int FlipWithinRow(int index, int columns)
    {
        var col = index % columns;
        return (index - col) + (columns - 1 - col);
    }
}
