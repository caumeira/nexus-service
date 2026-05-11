namespace Qos.Service.Lighting.Engine.Effects;

public sealed class StaticEffect : IEffect
{
    private readonly byte _r, _g, _b;
    public StaticEffect(byte r, byte g, byte b) { _r = r; _g = g; _b = b; }
    public string Name => "static";
    public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Fill(_r, _g, _b);
    public void Dispose() { }
}
