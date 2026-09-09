using System;
using Nexus.Service.Platform;
using Silk.NET.GLFW;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Outcome of <see cref="GlfwErrorGuard.Install"/>.</summary>
/// <param name="Installed">The callback was handed to GLFW without throwing.</param>
/// <param name="PreEmptedSilkDefault">The callback replaced was Silk.NET's, not ours.</param>
/// <param name="SelfTestPassed">A deliberate GLFW error reached our callback.</param>
internal readonly record struct GlfwGuardStatus(
    bool Installed, bool PreEmptedSilkDefault, bool SelfTestPassed, string Detail);

/// <summary>
/// Replaces Silk.NET's GLFW error callback, which throws GlfwException from
/// inside a native reverse-callback frame. No managed try/catch can intercept a
/// throw across that frame, so the runtime fast-fails the process (0xC0000409 on
/// Windows).
///
/// Install order is load-bearing: GlfwProvider.GetGlfw runs glfwInit and THEN
/// installs Silk's thrower, so a callback set before that is overwritten
/// (measured). Ours goes on GlfwProvider.UninitializedGLFW first, covering
/// glfwInit itself, and is re-asserted once GlfwProvider.GLFW has been forced.
/// </summary>
internal static class GlfwErrorGuard
{
    private static readonly object Gate = new();

    // static readonly, never reassigned: GLFW holds the marshalling stub for
    // this delegate for the process lifetime, and a collected delegate leaves the
    // native side calling into freed memory.
    private static readonly GlfwCallbacks.ErrorCallback Callback = OnGlfwError;

    // GLFW rejects an unknown window-hint target with InvalidEnum and stores
    // nothing, so provoking one is inert and proves the callback is live.
    private const int SelfTestHint = 0x7FFFFFFF;

    private static volatile bool _inSelfTest;
    private static int _count;
    private static ErrorCode _lastCode;
    private static string _lastDescription = string.Empty;

    /// <summary>Errors swallowed since process start.</summary>
    public static int SwallowedCount => System.Threading.Volatile.Read(ref _count);

    /// <summary>The error raised since <paramref name="mark"/> (a prior
    /// <see cref="SwallowedCount"/>). Scoped to one call so a failure that
    /// emitted nothing is not reported as an older, unrelated error.</summary>
    public static string ErrorSince(int mark) => SwallowedCount <= mark
        ? "no GLFW error reported"
        : $"{_lastCode} ({(int)_lastCode}): {_lastDescription}";

    public static GlfwGuardStatus Install()
    {
        lock (Gate)
        {
            try
            {
                // GlfwProvider.GetGlfw runs glfwInit and THEN installs Silk's
                // thrower, so ours goes on the uninitialized instance first
                // (covering glfwInit) and is re-asserted once the provider has
                // run. Installing only before it is silently overwritten.
                var glfw = GlfwProvider.UninitializedGLFW.Value;
                glfw.SetErrorCallback(Callback);
                _ = GlfwProvider.GLFW.Value;
                var replaced = glfw.SetErrorCallback(Callback);
                var preEmpted = replaced is not null && !ReferenceEquals(replaced, Callback);

                var before = SwallowedCount;
                _inSelfTest = true;
                try
                { glfw.WindowHint((WindowHintInt)SelfTestHint, 0); }
                finally { _inSelfTest = false; }
                var passed = SwallowedCount > before;

                return new GlfwGuardStatus(true, preEmpted, passed,
                    passed ? "guard live" : "guard installed but the self-test error never arrived");
            }
            catch (Exception ex)
            {
                return new GlfwGuardStatus(false, false, false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Nothing here may throw: this runs on a native reverse-callback frame,
    // which is the frame the whole class exists to keep exceptions out of.
    private static void OnGlfwError(ErrorCode code, string description)
    {
        try
        {
            if (_inSelfTest)
            {
                System.Threading.Interlocked.Increment(ref _count);
                return;
            }
            _lastCode = code;
            _lastDescription = description ?? string.Empty;
            System.Threading.Interlocked.Increment(ref _count);
            // Raw int alongside the name: a future capture may carry a code this
            // enum does not know, and the number is what the driver reported.
            ServiceLog.Warn($"[gpu] GLFW error swallowed: {code} ({(int)code}): {_lastDescription}");
        }
        catch { }
    }
}
