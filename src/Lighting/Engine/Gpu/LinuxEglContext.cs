using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Offscreen OpenGL 3.3 core context on Linux via EGL on a GPU **device
/// platform** (EGL_PLATFORM_DEVICE_EXT) — fully headless: no X server, no
/// Wayland, no window. This is what lets the lighting shader effects render
/// under the root system daemon, where GLFW (which needs a display) can't (and
/// crashes creating an nvidia GL context as root on the user's XWayland).
///
/// Direct analog of <see cref="MacGlContext"/> (CGL) — both create a windowless
/// GL context the dedicated GL worker thread can make current. Hardware
/// accelerated on the GPU device (probe showed RENDERER=NVIDIA …). Blittable
/// P/Invoke + unmanaged function pointers only — AOT-safe; compiles everywhere
/// (libEGL is resolved lazily, only used on Linux).
/// </summary>
internal static unsafe class LinuxEglContext
{
    private const string Egl = "libEGL.so.1";

    private const int EGL_NONE = 0x3038;
    private const uint EGL_OPENGL_API = 0x30A2;
    private const int EGL_OPENGL_BIT = 0x0008;
    private const int EGL_PBUFFER_BIT = 0x0001;
    private const int EGL_SURFACE_TYPE = 0x3033;
    private const int EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_RED_SIZE = 0x3024;
    private const int EGL_GREEN_SIZE = 0x3023;
    private const int EGL_BLUE_SIZE = 0x3022;
    private const int EGL_CONTEXT_MAJOR_VERSION = 0x3098;
    private const int EGL_CONTEXT_MINOR_VERSION = 0x30FB;
    private const int EGL_CONTEXT_OPENGL_PROFILE_MASK = 0x30FD;
    private const int EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT = 0x0001;
    private const uint EGL_PLATFORM_DEVICE_EXT = 0x313F;

    [DllImport(Egl)] private static extern IntPtr eglGetProcAddress(string name);
    [DllImport(Egl)] private static extern int eglInitialize(IntPtr dpy, int* major, int* minor);
    [DllImport(Egl)] private static extern int eglBindAPI(uint api);
    [DllImport(Egl)] private static extern int eglChooseConfig(IntPtr dpy, int* attribs, IntPtr* configs, int max, int* num);
    [DllImport(Egl)] private static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr config, IntPtr share, int* attribs);
    [DllImport(Egl)] private static extern int eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);
    [DllImport(Egl)] private static extern int eglDestroyContext(IntPtr dpy, IntPtr ctx);
    [DllImport(Egl)] private static extern int eglTerminate(IntPtr dpy);
    [DllImport(Egl)] private static extern int eglGetError();

    private static IntPtr _display;
    private static IntPtr _context;

    /// <summary>Create + make-current a headless GL 3.3 core context on the
    /// calling thread. Walks the EGL device list until one yields a context.</summary>
    public static void CreateAndMakeCurrent()
    {
        // Device-platform entrypoints are extensions, fetched at runtime.
        var queryDevices = (delegate* unmanaged<int, IntPtr*, int*, int>)eglGetProcAddress("eglQueryDevicesEXT");
        var getPlatformDisplay = (delegate* unmanaged<uint, IntPtr, int*, IntPtr>)eglGetProcAddress("eglGetPlatformDisplayEXT");
        if (queryDevices == null || getPlatformDisplay == null)
            throw new InvalidOperationException("EGL device platform unavailable (no EGL_EXT_device_base / EGL_EXT_platform_device)");

        IntPtr* devices = stackalloc IntPtr[16];
        int num = 0;
        if (queryDevices(16, devices, &num) == 0 || num == 0)
            throw new InvalidOperationException("EGL: no render devices");

        for (var i = 0; i < num; i++)
        {
            var dpy = getPlatformDisplay(EGL_PLATFORM_DEVICE_EXT, devices[i], null);
            if (dpy == IntPtr.Zero || eglInitialize(dpy, null, null) == 0)
                continue;
            eglBindAPI(EGL_OPENGL_API); // desktop GL, not GLES

            int* cfgAttr = stackalloc int[]
            {
                EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
                EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_NONE,
            };
            IntPtr config; int n = 0;
            if (eglChooseConfig(dpy, cfgAttr, &config, 1, &n) == 0 || n < 1)
            { eglTerminate(dpy); continue; }

            int* ctxAttr = stackalloc int[]
            {
                EGL_CONTEXT_MAJOR_VERSION, 3, EGL_CONTEXT_MINOR_VERSION, 3,
                EGL_CONTEXT_OPENGL_PROFILE_MASK, EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT, EGL_NONE,
            };
            var ctx = eglCreateContext(dpy, config, IntPtr.Zero, ctxAttr);
            if (ctx == IntPtr.Zero) { eglTerminate(dpy); continue; }

            // Surfaceless current — render goes to an FBO, no EGL surface.
            if (eglMakeCurrent(dpy, IntPtr.Zero, IntPtr.Zero, ctx) == 0)
            { eglDestroyContext(dpy, ctx); eglTerminate(dpy); continue; }

            _display = dpy;
            _context = ctx;
            return;
        }
        throw new InvalidOperationException($"EGL: no device produced a current GL context (last error 0x{eglGetError():X})");
    }

    public static void Destroy()
    {
        try
        {
            if (_context != IntPtr.Zero)
            {
                eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                eglDestroyContext(_display, _context);
            }
            if (_display != IntPtr.Zero)
                eglTerminate(_display);
        }
        catch { }
        _context = _display = IntPtr.Zero;
    }

    private static IntPtr _libgl;

    /// <summary>GL symbol loader for Silk.NET. On a GLVND stack eglGetProcAddress
    /// returns core GL functions too; dlsym(libGL) is a belt-and-suspenders
    /// fallback for any the driver won't hand back.</summary>
    public static IntPtr LoadGlSymbol(string name)
    {
        var p = eglGetProcAddress(name);
        if (p != IntPtr.Zero)
            return p;
        if (_libgl == IntPtr.Zero && !NativeLibrary.TryLoad("libGL.so.1", out _libgl))
            return IntPtr.Zero;
        return NativeLibrary.TryGetExport(_libgl, name, out var addr) ? addr : IntPtr.Zero;
    }
}

/// <summary>
/// Silk.NET <see cref="Silk.NET.Core.Contexts.INativeContext"/> resolving GL
/// function pointers through <see cref="LinuxEglContext.LoadGlSymbol"/>, once
/// the EGL context is current — mirror of <see cref="CglNativeContext"/>.
/// </summary>
internal sealed class EglNativeContext : Silk.NET.Core.Contexts.INativeContext
{
    public nint GetProcAddress(string procName, int? slot = null)
    {
        nint p = LinuxEglContext.LoadGlSymbol(procName);
        if (p == 0)
            throw new InvalidOperationException($"EGL GL symbol not found: {procName}");
        return p;
    }

    public bool TryGetProcAddress(string procName, out nint procAddress, int? slot = null)
    {
        procAddress = LinuxEglContext.LoadGlSymbol(procName);
        return procAddress != 0;
    }

    public void Dispose() { }
}
