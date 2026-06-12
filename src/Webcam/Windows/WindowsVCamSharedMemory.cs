using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;
using System.Runtime.Versioning;

namespace Nexus.Service.Webcam.Windows;

/// <summary>
/// Creates the Global namespace shared-memory section and frame-ready event
/// with the protocol SDDL (LocalService - the Frame Server identity - needs
/// read on both). Direct CreateFileMappingW because the managed
/// MemoryMappedFile API lost named-object security descriptors in .NET Core;
/// the view is exposed as Memory&lt;byte&gt; so VCamRingWriter stays
/// platform-free. Creating Global objects needs SeCreateGlobalPrivilege,
/// which any service account has; an unelevated interactive run does not.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsVCamSharedMemory : IVCamFrameRing
{
    private readonly nint _mapping;
    private readonly nint _view;
    private readonly nint _frameEvent;
    private readonly ViewMemoryManager _memory;
    private bool _disposed;

    private WindowsVCamSharedMemory(nint mapping, nint view, nint frameEvent, int length)
    {
        _mapping = mapping;
        _view = view;
        _frameEvent = frameEvent;
        _memory = new ViewMemoryManager(view, length);
    }

    public Memory<byte> Mapping => _memory.Memory;

    public static WindowsVCamSharedMemory Create(int width, int height, int slotCount)
    {
        var slotBytes = VCamProtocol.Nv12FrameBytes(width, height);
        var totalBytes = VCamProtocol.MappingBytes(slotBytes, slotCount);

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(VCamProtocol.SharedMemorySddl, SddlRevision1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "invalid camera frame ring SDDL");
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = (uint)Unsafe.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };
            var mapping = CreateFileMappingW(InvalidHandleValue, in attributes, PageReadWrite,
                (uint)(totalBytes >> 32), (uint)totalBytes, VCamProtocol.SharedMemoryName);
            var createError = Marshal.GetLastPInvokeError();
            if (mapping == 0)
            {
                throw new Win32Exception(createError,
                    "failed to create the camera frame ring (the Global namespace needs a service or elevated process)");
            }
            if (createError == ErrorAlreadyExists)
            {
                // A stale section survived (FrameServer still holds a handle):
                // we reopened it and our SDDL/size were ignored. The header
                // re-init below keeps the protocol coherent; log so a size or
                // ACL mismatch is diagnosable.
                ServiceLog.Warn("webcam ring: reopened existing shared-memory section (stale consumer handle)");
            }
            var view = MapViewOfFile(mapping, FileMapAllAccess, 0, 0, 0);
            if (view == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                CloseHandle(mapping);
                throw new Win32Exception(error, "failed to map the camera frame ring");
            }
            // Wake hint only; the consumer pulls at the negotiated rate, so a
            // creation failure here is tolerated rather than fatal.
            var frameEvent = CreateEventW(in attributes, 0, 0, VCamProtocol.FrameEventName);
            return new WindowsVCamSharedMemory(mapping, view, frameEvent, (int)totalBytes);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    public void SignalFrameReady()
    {
        if (_frameEvent != 0)
            SetEvent(_frameEvent);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ((IDisposable)_memory).Dispose();
        UnmapViewOfFile(_view);
        CloseHandle(_mapping);
        if (_frameEvent != 0)
            CloseHandle(_frameEvent);
    }

    /// <summary>Memory over the raw mapped view; lifetime is owned by the enclosing ring.</summary>
    private sealed unsafe class ViewMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _pointer;
        private readonly int _length;

        public ViewMemoryManager(nint pointer, int length)
        {
            _pointer = (byte*)pointer;
            _length = length;
        }

        public override Span<byte> GetSpan() => new(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    private const nint InvalidHandleValue = -1;
    private const uint SddlRevision1 = 1;
    private const int ErrorAlreadyExists = 183;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapAllAccess = 0x000F001F;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out nint descriptor, out uint descriptorBytes);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFileMappingW(
        nint file, in SecurityAttributes attributes, uint protect, uint maximumSizeHigh, uint maximumSizeLow, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(nint mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytesToMap);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint baseAddress);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateEventW(in SecurityAttributes attributes, int manualReset, int initialState, string name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

/// <summary>Live ring factory used by the production virtual camera.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsVCamRingFactory : IVCamFrameRingFactory
{
    public IVCamFrameRing Create(int width, int height, int slotCount) =>
        WindowsVCamSharedMemory.Create(width, height, slotCount);
}
