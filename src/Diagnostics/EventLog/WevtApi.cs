#if WINDOWS
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Diagnostics.EventLog;

/// <summary>
/// Raw P/Invoke surface for the Windows Event Log API (wevtapi.dll): query a
/// channel by XPath, render an event to XML, and subscribe to live events.
/// wevtapi's exports are Unicode-only (no A/W suffix pair), so CharSet.Unicode
/// on each string-taking method is the only marshalling needed; matches the
/// DllImport + SafeHandle-return style already used for CreateFileW in
/// Slv3WinUsbInterop.cs, so this stays AOT-safe without a COM EventLogReader.
/// </summary>
internal static class WevtApi
{
    public const uint EvtQueryChannelPath = 0x1;
    public const uint EvtQueryReverseDirection = 0x200;
    public const uint EvtRenderEventXml = 1;
    public const uint EvtSubscribeToFutureEvents = 1;
    public const int EvtSubscribeActionError = 0;
    public const int EvtSubscribeActionDeliver = 1;

    /// <summary>Owns an EVT_HANDLE; ReleaseHandle calls EvtClose.</summary>
    public sealed class SafeEvtHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeEvtHandle() : base(ownsHandle: true)
        {
        }

        public SafeEvtHandle(IntPtr existingHandle, bool ownsHandle) : base(ownsHandle)
        {
            SetHandle(existingHandle);
        }

        protected override bool ReleaseHandle() => EvtClose(handle);
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeEvtHandle EvtQuery(IntPtr session, string? path, string? query, uint flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EvtNext(
        SafeEvtHandle resultSet, uint eventArraySize, [Out] IntPtr[] eventArray,
        uint timeout, uint flags, out uint numReturned);

    // fragment is passed as SafeEvtHandle (borrowed, not owned) so the
    // marshaller pins it for the duration of the call without closing it.
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EvtRender(
        IntPtr context, SafeEvtHandle fragment, uint flags, uint bufferSize,
        IntPtr buffer, out uint bufferUsed, out uint propertyCount);

    // callback is a raw function pointer (see EventLogMonitor.OnEvent, an
    // [UnmanagedCallersOnly] static method cast via delegate*) rather than a
    // marshalled delegate, so no runtime marshalling stub is generated.
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeEvtHandle EvtSubscribe(
        IntPtr session, IntPtr signalEvent, string channelPath, string? query,
        IntPtr bookmark, IntPtr context, IntPtr callback, uint flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EvtClose(IntPtr handle);
}
#endif
