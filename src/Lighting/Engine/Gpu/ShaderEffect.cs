using System;
using Silk.NET.OpenGL;

namespace Qos.Service.Lighting.Engine.Gpu;

/// <summary>
/// An <see cref="IEffect"/> backed by a fragment shader. Compiles lazily on
/// first <see cref="RenderFrame"/>, renders a full-screen triangle strip to
/// the shared FBO, and glReadPixels into the CPU-side CanvasBuffer.
///
/// The shader is authored as if (0,0) is the top-left pixel - the readback
/// path flips rows on its way into the CanvasBuffer so downstream device
/// sampling keeps the same top-left convention.
///
/// GPU-only. If the shared <see cref="GpuContext"/> can't initialise or the
/// shader fails to compile, the canvas stays dark; there is no CPU fallback.
/// </summary>
public sealed class ShaderEffect : IEffect
{
    private const string VertexShaderSrc = """
        #version 330 core
        layout (location = 0) in vec2 a_pos;
        void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }
        """;

    private readonly GpuContext _ctx;
    private readonly string _fragSource;
    private readonly Action<GL, int, double>? _setUniforms;

    private uint _program;
    private int _uResolution = -1;
    private int _uTime = -1;
    private int _uSpeed = -1;
    private int _uIntensity = -1;
    private int _uHue = -1;
    private int _uColorize = -1;
    private int _uSaturation = -1;
    private int _uContrast = -1;
    private int _uAudioLevel = -1;
    private int _uAudioBass = -1;
    private int _uAudioMid = -1;
    private int _uAudioHigh = -1;
    private int _uAudioBeat = -1;
    private int _uAudioBoost = -1;
    private int _uSpectrum = -1;
    private readonly System.Collections.Generic.Dictionary<string, int> _uniformCache = new();
    private bool _compiled;
    private bool _failed;
    private byte[] _readback = Array.Empty<byte>();
    private byte[] _flipBuffer = Array.Empty<byte>();

    // Cached Invoke-callback that reads per-frame args from instance fields.
    // Avoids allocating a fresh lambda (display class) every render frame.
    // RenderFrame is only called from the engine loop thread, so stashing
    // args on the instance is safe.
    private CanvasBuffer? _pendingCanvas;
    private double _pendingTick;
    private Action? _cachedRenderDelegate;

    public string Name { get; }
    public float Speed { get; set; } = 1f;
    public float Intensity { get; set; } = 1f;
    public float Hue { get; set; } = 0f;
    public float Colorize { get; set; } = 0f;
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
    /// <summary>0 = ignore audio, 1 = full reactive. Bound every frame to u_audioBoost.</summary>
    public float AudioBoost { get; set; } = 0f;
    /// <summary>Per-effect uniforms keyed by GLSL name (e.g. "u_zoom", "u_freq").</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, float>? ExtraParams { get; set; }

    public ShaderEffect(string name, GpuContext ctx, string fragSource,
        Action<GL, int, double>? setUniforms = null)
    {
        Name = name;
        _ctx = ctx;
        _fragSource = fragSource;
        _setUniforms = setUniforms;
    }

    private int _frameCount;

    public void RenderFrame(CanvasBuffer canvas, double tickMs)
    {
        if (_failed)
        {
            // GPU unavailable / shader broken - canvas stays as the caller left it.
            return;
        }

        try
        {
            RenderFrameInternal(canvas, tickMs);
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu/{Name}] RenderFrame threw: {ex}");
            _failed = true;
        }
    }

    private void RenderFrameInternal(CanvasBuffer canvas, double tickMs)
    {
        lock (_ctx.Lock)
        {
            _ctx.EnsureInitializedLocked();
            if (!_ctx.Available)
            {
                _failed = true;
                return;
            }
        }

        _pendingCanvas = canvas;
        _pendingTick = tickMs;
        _cachedRenderDelegate ??= RenderPending;
        _ctx.Invoke(_cachedRenderDelegate);
    }

    private void RenderPending() => RenderOnGlThread(_pendingCanvas!, _pendingTick);

    private void RenderOnGlThread(CanvasBuffer canvas, double tickMs)
    {
        {
            var gl = _ctx.Gl;

            if (!_compiled)
            {
                try
                {
                    GpuContext.Log($"[gpu/{Name}] compiling shader");
                    CompileProgram(gl);
                    _compiled = true;
                    GpuContext.Log($"[gpu/{Name}] shader compiled, prog={_program}");
                }
                catch (Exception ex)
                {
                    GpuContext.Log($"[gpu/{Name}] shader compile failed: {ex}");
                    _failed = true;
                    return;
                }
            }

            int w = _ctx.Width;
            int h = _ctx.Height;

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ctx.Fbo);
            gl.Viewport(0, 0, (uint)w, (uint)h);
            gl.UseProgram(_program);
            if (_uResolution >= 0)
            {
                gl.Uniform2(_uResolution, (float)w, (float)h);
            }
            if (_uTime >= 0)
            {
                gl.Uniform1(_uTime, (float)(tickMs % 86_400_000 / 1000.0));
            }
            if (_uSpeed >= 0)
            {
                gl.Uniform1(_uSpeed, Speed);
            }
            if (_uIntensity >= 0)
            {
                gl.Uniform1(_uIntensity, Intensity);
            }
            if (_uHue >= 0)
            {
                gl.Uniform1(_uHue, Hue);
            }
            if (_uColorize >= 0)
            {
                gl.Uniform1(_uColorize, Colorize);
            }
            if (_uSaturation >= 0)
            {
                gl.Uniform1(_uSaturation, Saturation);
            }
            if (_uContrast >= 0)
            {
                gl.Uniform1(_uContrast, Contrast);
            }
            if (_uAudioLevel >= 0)
            {
                gl.Uniform1(_uAudioLevel, Qos.Service.Lighting.Engine.AudioState.Level);
            }
            if (_uAudioBass >= 0)
            {
                gl.Uniform1(_uAudioBass, Qos.Service.Lighting.Engine.AudioState.Bass);
            }
            if (_uAudioMid >= 0)
            {
                gl.Uniform1(_uAudioMid, Qos.Service.Lighting.Engine.AudioState.Mid);
            }
            if (_uAudioHigh >= 0)
            {
                gl.Uniform1(_uAudioHigh, Qos.Service.Lighting.Engine.AudioState.High);
            }
            if (_uAudioBeat >= 0)
            {
                gl.Uniform1(_uAudioBeat, Qos.Service.Lighting.Engine.AudioState.Beat);
            }
            if (_uAudioBoost >= 0)
            {
                gl.Uniform1(_uAudioBoost, AudioBoost);
            }
            if (_uSpectrum >= 0)
            {
                unsafe
                {
                    fixed (float* p = Qos.Service.Lighting.Engine.AudioState.Spectrum)
                    {
                        gl.Uniform1(_uSpectrum, (uint)Qos.Service.Lighting.Engine.AudioState.SpectrumLength, p);
                    }
                }
            }
            if (ExtraParams is not null)
            {
                foreach (var kv in ExtraParams)
                {
                    int loc;
                    if (!_uniformCache.TryGetValue(kv.Key, out loc))
                    {
                        loc = gl.GetUniformLocation(_program, kv.Key);
                        _uniformCache[kv.Key] = loc;
                    }
                    if (loc >= 0)
                    {
                        gl.Uniform1(loc, kv.Value);
                    }
                }
            }
            _setUniforms?.Invoke(gl, (int)_program, tickMs);

            gl.Disable(EnableCap.Blend);
            gl.Disable(EnableCap.DepthTest);
            // Defensive: drivers vary on whether culling stays disabled
            // across GL state batches; explicitly off per-draw so the
            // fullscreen strip never gets silently culled.
            gl.Disable(EnableCap.CullFace);
            gl.BindVertexArray(_ctx.QuadVao);
            gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            int bytes = w * h * 3;
            if (_readback.Length < bytes)
            {
                _readback = new byte[bytes];
                _flipBuffer = new byte[bytes];
            }
            unsafe
            {
                fixed (byte* p = _readback)
                {
                    gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
                    gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgb, PixelType.UnsignedByte, p);
                }
            }

            // GL origin is bottom-left - flip rows into the top-left convention the
            // rest of the pipeline (and the old CPU effects) use.
            int rowBytes = w * 3;
            for (int y = 0; y < h; y++)
            {
                int srcRow = (h - 1 - y) * rowBytes;
                int dstRow = y * rowBytes;
                System.Buffer.BlockCopy(_readback, srcRow, _flipBuffer, dstRow, rowBytes);
            }

            if (canvas.Width == w && canvas.Height == h)
            {
                canvas.WriteFromRgb(_flipBuffer.AsSpan(0, bytes));
            }
            // Canvas resized outside what we rendered: drop this frame silently.

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            _frameCount++;
            if (_frameCount == 1 || _frameCount == 5 || _frameCount == 30 || _frameCount == 100 || _frameCount % 300 == 0)
            {
                GpuContext.Log($"[gpu/{Name}] frame #{_frameCount} rendered");
            }
        }
    }

    private void CompileProgram(GL gl)
    {
        uint vs = CompileShader(gl, ShaderType.VertexShader, VertexShaderSrc);
        uint fs = CompileShader(gl, ShaderType.FragmentShader, _fragSource);
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, GLEnum.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            throw new InvalidOperationException($"link failed: {log}");
        }
        gl.DetachShader(prog, vs);
        gl.DetachShader(prog, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        _program = prog;
        _uResolution = gl.GetUniformLocation(prog, "u_resolution");
        _uTime = gl.GetUniformLocation(prog, "u_time");
        _uSpeed = gl.GetUniformLocation(prog, "u_speed");
        _uIntensity = gl.GetUniformLocation(prog, "u_intensity");
        _uHue = gl.GetUniformLocation(prog, "u_hue");
        _uColorize = gl.GetUniformLocation(prog, "u_colorize");
        _uSaturation = gl.GetUniformLocation(prog, "u_saturation");
        _uContrast = gl.GetUniformLocation(prog, "u_contrast");
        _uAudioLevel = gl.GetUniformLocation(prog, "u_audioLevel");
        _uAudioBass = gl.GetUniformLocation(prog, "u_audioBass");
        _uAudioMid = gl.GetUniformLocation(prog, "u_audioMid");
        _uAudioHigh = gl.GetUniformLocation(prog, "u_audioHigh");
        _uAudioBeat = gl.GetUniformLocation(prog, "u_audioBeat");
        _uAudioBoost = gl.GetUniformLocation(prog, "u_audioBoost");
        _uSpectrum = gl.GetUniformLocation(prog, "u_spectrum[0]");
        if (_uSpectrum < 0)
        {
            _uSpectrum = gl.GetUniformLocation(prog, "u_spectrum");
        }
    }

    private static uint CompileShader(GL gl, ShaderType type, string src)
    {
        GpuContext.Log($"[gpu] CompileShader {type}, source length={src.Length}, last chars=<{(src.Length > 60 ? src.Substring(src.Length - 60) : src)}>");
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = gl.GetShaderInfoLog(s);
            gl.DeleteShader(s);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }
        return s;
    }

    public void Dispose()
    {
        if (_program != 0 && _ctx.Available)
        {
            try
            {
                _ctx.Invoke(() => _ctx.Gl.DeleteProgram(_program));
            }
            catch { }
            _program = 0;
        }
    }
}
