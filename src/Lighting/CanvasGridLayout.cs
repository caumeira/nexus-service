using System;

namespace Nexus.Service.Lighting;

/// <summary>
/// Default device-frame positions on the 1000x600 lighting canvas. The grid
/// scales with the total device count so every card lands in a distinct,
/// guaranteed-on-canvas cell — replaces the per-provider Default*Layout
/// modulo-wrap that used to stack overflow cards on top of existing slots.
/// </summary>
internal static class CanvasGridLayout
{
    private const float CanvasW = 1000f;
    private const float CanvasH = 600f;
    private const float Pad = 12f;
    private const float MaxCardW = 240f;
    private const float MaxCardH = 60f;

    public static (float x, float y, float w, float h) Slot(int index, int totalCount)
    {
        if (totalCount <= 0 || index < 0) return (Pad, Pad, MaxCardW, MaxCardH);

        var n = Math.Max(1, totalCount);
        var availW = CanvasW - 2f * Pad;
        var availH = CanvasH - 2f * Pad;
        // cols ≈ sqrt(n * aspect) so the grid mirrors the canvas shape.
        var aspect = availW / availH;
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * aspect)));
        var rows = Math.Max(1, (int)Math.Ceiling((float)n / cols));

        var cellW = availW / cols;
        var cellH = availH / rows;
        // Card sized as a fraction of the cell — no floor so high-N grids can shrink
        // cards arbitrarily without spilling out of their cell. MaxCard caps low-N from
        // ballooning to canvas-size.
        var cardW = Math.Min(MaxCardW, cellW * 0.92f);
        var cardH = Math.Min(MaxCardH, cellH * 0.7f);

        var s = ((index % (cols * rows)) + cols * rows) % (cols * rows);
        var col = s % cols;
        var row = s / cols;

        var x = Pad + col * cellW + (cellW - cardW) * 0.5f;
        var y = Pad + row * cellH + (cellH - cardH) * 0.5f;

        // Defensive clamp — the min-card floors can push a card slightly past the cell edge at very high counts.
        if (x + cardW > CanvasW - Pad) x = CanvasW - Pad - cardW;
        if (y + cardH > CanvasH - Pad) y = CanvasH - Pad - cardH;
        if (x < Pad) x = Pad;
        if (y < Pad) y = Pad;

        return (x, y, cardW, cardH);
    }
}
