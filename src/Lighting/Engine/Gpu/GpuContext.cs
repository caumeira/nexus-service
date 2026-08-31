using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Platform;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Owns a single shared offscreen OpenGL 3.3 core context used by every shader
/// effect. Thread-safety: all GL work runs on the dedicated <c>_glThread</c>;
/// callers off that thread go through <see cref="Invoke"/>, which marshals the
/// work onto it and blocks until completion. <see cref="Lock"/> only guards
/// lazy initialization.
///
/// If init fails the context is marked unavailable and shader effects do not
/// render at all - there is no CPU fallback.
/// </summary>
public sealed class GpuContext : IDisposable
{
    private readonly object _lock = new();
    private readonly int _width;
    private readonly int _height;
#if MACOS
    // macOS path: CGL context pointer, no window.
    private IntPtr _cglCtx;
#elif LINUX
    // Linux path: headless EGL device context, no window (works under the root daemon).
    private bool _eglUsed;
#else
    // Windows path: GLFW hidden window owns the context.
    private IWindow? _window;
#endif
    private GL? _gl;
    private uint _fbo;
    private uint _fboTex;
    private uint _quadVao;
    private uint _quadVbo;
    private volatile bool _initStarted;
    private volatile bool _ready;
    private volatile bool _failed;
    private bool _retryUsed;
    private volatile bool _disposed;

    // Dedicated GL thread. The engine loop is a Task that hops thread-pool
    // threads between awaits, but GLFW/wgl contexts are sticky to the
    // thread that created them, so every GL call must marshal to this one
    // thread via Invoke().
    private Thread? _glThread;
    private readonly BlockingCollection<Action> _workQueue = new(new ConcurrentQueue<Action>());
    private readonly ManualResetEventSlim _initDone = new(false);
    private Exception? _initError;

    // How long a caller BLOCKS waiting for the context. Not a verdict on the
    // card: the thread keeps going and Available flips on its own if it lands
    // late. Sized off a cold-boot AMD iGPU measured at 28.4s.
    public TimeSpan InitTimeout { get; set; } = TimeSpan.FromSeconds(60);

    // GL_RENDERER of the bound context (which physical card was selected), set
    // once init succeeds. Null until then.
    public string? Renderer { get; private set; }


    // Per-thread reusable completion handle used by Invoke(). Invoke blocks the
    // caller until its work runs, so at most one outstanding per thread -- safe
    // to reuse without pooling. Avoids allocating a fresh MRE (~60 bytes +
    // internal Lazy<ManualResetEvent>) on every render frame.
    private readonly ThreadLocal<ManualResetEventSlim> _invokeDone =
        new(() => new ManualResetEventSlim(false), trackAllValues: true);

    // Optional: lets the Linux EGL path read the saved render-GPU choice. Null
    // in contexts that don't need it (e.g. tests).
    private readonly Nexus.Service.Persistence.IConfigStore? _store;

    // Substituted by tests: a real GL init is host-dependent, and concurrent
    // glfwInit calls in a parallelized suite are undefined.
    private readonly Action _initAction;

    public GpuContext(int width, int height, Nexus.Service.Persistence.IConfigStore? store = null)
        : this(width, height, store, null) { }

    internal GpuContext(int width, int height, Nexus.Service.Persistence.IConfigStore? store, Action? initAction)
    {
        _width = width;
        _height = height;
        _store = store;
        _initAction = initAction ?? InitInternal;
    }

    public object Lock => _lock;
    public int Width => _width;
    public int Height => _height;
    public uint Fbo => _fbo;
    public uint QuadVao => _quadVao;
    public GL Gl => _gl ?? throw new InvalidOperationException("GpuContext not initialized");
    public bool Available => _ready && !_failed && !_disposed;

    /// <summary>An init attempt has run and thrown. Distinct from "not ready
    /// yet": only this is terminal for the card.</summary>
    public bool Failed => _failed;

    /// <summary>An attempt is running and has neither succeeded nor thrown.</summary>
    public bool Initializing => _initStarted && !_ready && !_failed && !_disposed;

    // Co-located with nexus-service.log; ServiceLog.LogsDirectory resolves the
    // writable per-OS logs dir (HOME-based on Linux, so the old /usr/share
    // read-only concern doesn't apply).
    private static readonly string LogPath = System.IO.Path.Combine(ServiceLog.LogsDirectory, "nexus-gpu.log");

    public static void Log(string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            System.IO.File.AppendAllText(LogPath, $"{DateTime.UtcNow:HH:mm:ss.fff} {line}\n");
        }
        catch { }
        // These are GL init/shader traces, not failures - INF, not ERR. Full
        // detail still lands in the dedicated nexus-gpu.log above.
        ServiceLog.Info(line);
    }

    /// <summary>
    /// Start GL init on the dedicated nexus-gl thread. Returns immediately; a
    /// caller that needs the context ready follows with <see cref="WaitForInit"/>
    /// OUTSIDE the lock. Must hold <see cref="Lock"/>.
    /// </summary>
    public void EnsureInitializedLocked()
    {
        if (_initStarted || _disposed)
        {
            return;
        }

        _initStarted = true;
        _glThread = new Thread(GlThreadMain) { IsBackground = true, Name = "nexus-gl" };
        _glThread.Start();
    }

    /// <summary>Block until the attempt finishes or <paramref name="timeout"/>
    /// elapses; the thread keeps running past it and <see cref="Available"/>
    /// flips on its own. Call WITHOUT holding <see cref="Lock"/>, which the
    /// render path takes every frame.</summary>
    public bool WaitForInit(TimeSpan timeout) => WaitForInit(timeout, CancellationToken.None);

    /// <inheritdoc cref="WaitForInit(TimeSpan)"/>
    public bool WaitForInit(TimeSpan timeout, CancellationToken ct)
    {
        if (!_initDone.Wait(timeout, ct))
        {
            Log($"[gpu] context init still running after {timeout.TotalSeconds:0.#}s; "
                + "shader effects stay dark until it lands");
            return false;
        }
        if (_initError is not null)
        {
            Log($"[gpu] context init failed: {_initError.Message}");
        }
        return Available;
    }

    /// <summary>Abandon a FAILED attempt so one more can run, once per process.
    /// Only a terminated attempt qualifies: glfwInit/glfwCreateWindow share
    /// process-global state, so a second init beside a still-running one is
    /// undefined.</summary>
    public bool ResetForRetry()
    {
        lock (_lock)
        {
            if (_disposed || _retryUsed || !_failed)
            {
                return false;
            }
            _retryUsed = true;
            _initError = null;
            _failed = false;
            _ready = false;
            _initDone.Reset();
            _initStarted = false;
            _gl = null;
            Renderer = null;
            _fbo = 0;
            _fboTex = 0;
            _quadVao = 0;
            _quadVbo = 0;
#if MACOS
            _cglCtx = IntPtr.Zero;
#elif LINUX
            _eglUsed = false;
#else
            // Deliberately not disposed: GLFW window destruction belongs to the
            // thread that created it, and that thread has exited. One leaked
            // hidden window per process is harmless; reaching across threads to
            // free it is not.
            _window = null;
#endif
            // Deliberately does NOT start the next attempt: the caller sets the
            // OS GPU preference (read at context-creation time) first.
            return true;
        }
    }

    /// <summary>Mark an attempt that never returned as failed; its thread stays
    /// parked in native init and dies with the process.</summary>
    public void AbandonInit()
    {
        lock (_lock)
        {
            if (_disposed || _ready || _failed || !_initStarted)
            {
                return;
            }
            _initError = new TimeoutException("GL context init never returned");
            _failed = true;
            _initDone.Set();
        }
    }

    private void GlThreadMain()
    {
        try
        { _initAction(); }
        catch (Exception ex)
        {
            // Published under the lock so ResetForRetry cannot land between the
            // flag write and the Set().
            lock (_lock)
            {
                _initError = ex;
                _failed = true;
                _initDone.Set();
            }
            return;
        }
        lock (_lock)
        {
            _ready = true;
            _initDone.Set();
        }

        while (!_disposed)
        {
            Action work;
            try
            { work = _workQueue.Take(); }
            catch (InvalidOperationException) { return; }
            try
            { work(); }
            catch (Exception ex) { Log($"[gpu] work item threw: {ex}"); }
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> on the dedicated GL thread and block the
    /// caller until it returns. The caller does not need to hold <see cref="Lock"/>.
    /// </summary>
    public void Invoke(Action work)
    {
        if (!Available || _glThread is null)
        {
            return;
        }
        if (Thread.CurrentThread == _glThread)
        {
            work();
            return;
        }
        Exception? caught = null;
        var done = _invokeDone.Value!;
        done.Reset();
        _workQueue.Add(() =>
        {
            try
            { work(); }
            catch (Exception ex) { caught = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (caught is not null)
        {
            throw caught;
        }
    }

    private void InitInternal()
    {
#if MACOS
        // macOS: skip GLFW / NSWindow entirely and create a headless GL 4.1
        // core context via CGL. Works from any thread, no AppKit needed.
        Log("[gpu] macOS: CGL create core context");
        _cglCtx = MacGlContext.CreateAndMakeCurrent();
        _gl = GL.GetApi(new CglNativeContext());
#elif LINUX
        // Linux: headless EGL on the GPU device platform - no X/Wayland, no
        // window. GLFW needs a display and crashes creating an nvidia GL
        // context as root on the user's XWayland, so the root daemon can't
        // use it; EGL device-platform is windowless like macOS's CGL.
        Log("[gpu] Linux: EGL device-platform headless context");
        LinuxEglContext.CreateAndMakeCurrent(_store?.Load().Lighting.RenderGpu ?? "auto");
        _eglUsed = true;
        _gl = GL.GetApi(new EglNativeContext());
#else
        // Windows: a hidden GLFW window owns the WGL context. With no usable GPU
        // (no dGPU + an "F"-SKU CPU with no iGPU, or only a virtual/indirect
        // display), GLFW context creation fail-fasts inside native code
        // (0xc0000409) - which a managed catch can't intercept, so it kills the
        // whole process. A removed card's driver stays registered, so DXGI still
        // enumerates it as a ghost adapter; probe D3D11CreateDevice (the ghost
        // fails it) and throw before touching GLFW so init fails cleanly. There
        // is no CPU shader fallback - shader effects just don't render.
        if (!Nexus.Service.Sensors.GpuAdapterLuids.HasUsableHardwareGpu())
        {
            throw new InvalidOperationException("no usable GPU adapter present");
        }
        Log("[gpu] Register GLFW platform");
        // Silk.NET normally registers the GLFW backend via module initializer,
        // but AOT strips that path - we have to register it explicitly or
        // Window.Create fails with "no suitable window platform".
        Silk.NET.Windowing.Glfw.GlfwWindowing.Use();
        var opts = WindowOptions.Default with
        {
            IsVisible = false,
            ShouldSwapAutomatically = false,
            VSync = false,
            Size = new Vector2D<int>(_width, _height),
            Title = "nexus-gpu",
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(3, 3)),
        };
        Log("[gpu] Window.Create");
        _window = Window.Create(opts);
        Log("[gpu] window.Initialize");
        _window.Initialize();
        Log("[gpu] CreateOpenGL");
        _gl = _window.CreateOpenGL();
#endif
        Log("[gpu] GL ready");
        try
        {
            // Which physical card the context bound to - the proof the
            // GpuPreference class steered selection, and a diagnostic on a
            // customer capture.
            Renderer = _gl!.GetStringS(StringName.Renderer);
            Log($"[gpu] GL context on renderer='{Renderer}' "
                + $"vendor='{_gl.GetStringS(StringName.Vendor)}' "
                + $"version='{_gl.GetStringS(StringName.Version)}'");
        }
        catch (Exception ex) { Log($"[gpu] GL renderer query failed: {ex.Message}"); }

        _fboTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _fboTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8,
                (uint)_width, (uint)_height, 0, PixelFormat.Rgb, PixelType.UnsignedByte, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _fboTex, 0);
        // Deterministic flat-shading across drivers (some Mesa/Intel paths
        // default to FirstVertexConvention).
        try
        { _gl.ProvokingVertex(VertexProvokingMode.LastVertexConvention); }
        catch { }
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"FBO incomplete: {status}");
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Fullscreen triangle strip (covers the whole clip-space quad)
        _quadVao = _gl.GenVertexArray();
        _quadVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_quadVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        Span<float> quad = stackalloc float[] { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
        unsafe
        {
            fixed (float* p = quad)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            }
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, (uint)(2 * sizeof(float)), (void*)0);
        }
        _gl.BindVertexArray(0);

        Log($"[gpu] OpenGL context ready ({_width}x{_height})");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        try
        { _workQueue.CompleteAdding(); }
        catch { }
        // If the GL thread didn't actually exit it may still be mid-GL-call with
        // the context current; destroying the native context underneath it
        // (eglTerminate / CGLDestroyContext) is undefined and can segfault. Only
        // tear it down once the thread has joined; a leaked context at process
        // exit is harmless.
        var joined = _glThread?.Join(TimeSpan.FromSeconds(2)) ?? true;
#if MACOS
        if (joined && _cglCtx != IntPtr.Zero)
        {
            MacGlContext.Destroy(_cglCtx);
            _cglCtx = IntPtr.Zero;
        }
#elif LINUX
        if (joined && _eglUsed)
        {
            LinuxEglContext.Destroy();
            _eglUsed = false;
        }
#else
        try
        { _window?.Dispose(); }
        catch { }
#endif
        // Dispose the per-thread MREs we created along the way.
        if (_invokeDone.Values is { } values)
        {
            foreach (var mre in values)
            {
                try
                { mre.Dispose(); }
                catch { }
            }
        }
        _invokeDone.Dispose();
    }
}
