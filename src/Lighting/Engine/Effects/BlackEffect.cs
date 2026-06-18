namespace Nexus.Service.Lighting.Engine.Effects;

/// <summary>
/// Outputs a solid-black canvas every frame. Name is "media" so GetSync()
/// returns "media" while the engine is running this effect.
/// </summary>
internal sealed class BlackEffect : IEffect
{
    public string Name => "media";
    public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Clear();
    public void Dispose() { }
}
