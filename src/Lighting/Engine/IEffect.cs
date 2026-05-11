using System;

namespace Qos.Service.Lighting.Engine;

public interface IEffect : IDisposable
{
    string Name { get; }
    void RenderFrame(CanvasBuffer canvas, double tickMs);
}
