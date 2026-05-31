using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
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
/// If init fails the context is marked unavailable and shader effects fall
/// back to their CPU implementations.
/// </summary>
public sealed class GpuContext : IDisposable
{
    private readonly object _lock = new();
    private readonly int _width;
    private readonly int _height;
    // Windows path: GLFW hidden window owns the context.
    private IWindow? _window;
    // macOS path: CGL context pointer, no window.
    private IntPtr _cglCtx;
    // Linux path: headless EGL device context, no window (works under the root daemon).
    private bool _eglUsed;
    private GL? _gl;
    private uint _fbo;
    private uint _fboTex;
    private uint _quadVao;
    private uint _quadVbo;
    private bool _initTried;
    private bool _failed;
    private bool _disposed;

    // Dedicated GL thread. The engine loop is a Task that hops thread-pool
    // threads between awaits, but GLFW/wgl contexts are sticky to the
    // thread that created them, so every GL call must marshal to this one
    // thread via Invoke().
    private Thread? _glThread;
    private readonly BlockingCollection<Action> _workQueue = new(new ConcurrentQueue<Action>());
    private readonly ManualResetEventSlim _initDone = new(false);
    private Exception? _initError;

    // Per-thread reusable completion handle used by Invoke(). Invoke blocks the
    // caller until its work runs, so at most one outstanding per thread -- safe
    // to reuse without pooling. Avoids allocating a fresh MRE (~60 bytes +
    // internal Lazy<ManualResetEvent>) on every render frame.
    private readonly ThreadLocal<ManualResetEventSlim> _invokeDone =
        new(() => new ManualResetEventSlim(false), trackAllValues: true);

    public GpuContext(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public object Lock => _lock;
    public int Width => _width;
    public int Height => _height;
    public uint Fbo => _fbo;
    public uint QuadVao => _quadVao;
    public GL Gl => _gl ?? throw new InvalidOperationException("GpuContext not initialized");
    public bool Available => _initTried && !_failed && !_disposed;

    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "gpu.log");

    public static void Log(string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            System.IO.File.AppendAllText(LogPath, $"{DateTime.UtcNow:HH:mm:ss.fff} {line}\n");
        }
        catch { }
        Console.Error.WriteLine(line);
    }

    /// <summary>
    /// Lazily init the GL context on first use. Must hold <see cref="Lock"/>.
    /// </summary>
    public void EnsureInitializedLocked()
    {
        if (_initTried || _disposed)
        {
            return;
        }

        _initTried = true;

        _glThread = new Thread(GlThreadMain) { IsBackground = true, Name = "nexus-gl" };
        _glThread.Start();
        if (!_initDone.Wait(TimeSpan.FromSeconds(10)))
        {
            Log("[gpu] context init timed out, falling back to CPU");
            _failed = true;
            return;
        }
        if (_initError is not null)
        {
            Log($"[gpu] context init failed: {_initError.Message}");
            _failed = true;
        }
    }

    private void GlThreadMain()
    {
        try
        { InitInternal(); }
        catch (Exception ex) { _initError = ex; _initDone.Set(); return; }
        _initDone.Set();

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
        if (OperatingSystem.IsMacOS())
        {
            // macOS: skip GLFW / NSWindow entirely and create a headless GL 4.1
            // core context via CGL. Works from any thread, no AppKit needed.
            Log("[gpu] macOS: CGL create core context");
            _cglCtx = MacGlContext.CreateAndMakeCurrent();
            _gl = GL.GetApi(new CglNativeContext());
        }
        else if (OperatingSystem.IsLinux())
        {
            // Linux: headless EGL on the GPU device platform — no X/Wayland, no
            // window. GLFW needs a display and crashes creating an nvidia GL
            // context as root on the user's XWayland, so the root daemon can't
            // use it; EGL device-platform is windowless like macOS's CGL.
            Log("[gpu] Linux: EGL device-platform headless context");
            LinuxEglContext.CreateAndMakeCurrent();
            _eglUsed = true;
            _gl = GL.GetApi(new EglNativeContext());
        }
        else
        {
            // Windows: hidden GLFW window owns the context.
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
        }
        Log("[gpu] GL ready");

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
        // defaulted to FirstVertexConvention historically).
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

    private void TeardownOnFailure()
    {
        try
        { _window?.Dispose(); }
        catch { }
        _window = null;
        _gl = null;
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
        // tear it down once the thread has joined — a leaked context at process
        // exit is harmless, a crash on shutdown is not.
        var joined = _glThread?.Join(TimeSpan.FromSeconds(2)) ?? true;
        try
        { _window?.Dispose(); }
        catch { }
        if (joined && _cglCtx != IntPtr.Zero)
        {
            MacGlContext.Destroy(_cglCtx);
            _cglCtx = IntPtr.Zero;
        }
        if (joined && _eglUsed)
        {
            LinuxEglContext.Destroy();
            _eglUsed = false;
        }
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
