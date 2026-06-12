// Common includes plus minimal dependency-free COM utilities.
// Deliberately no ATL/WIL/C++WinRT so the test PC builds offline with
// nothing beyond MSVC and the Windows SDK.

#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <windows.h>
#include <cguid.h>
#include <sddl.h>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <new>

#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfobjects.h>
#include <mftransform.h>
#include <mfvirtualcamera.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <propvarutil.h>

// ---------------------------------------------------------------------------
// Tracing: OutputDebugString based, viewable with DebugView/WinDbg attached to
// the FrameServer svchost. Compiled in always; cost is negligible at camera
// frame rates.
// ---------------------------------------------------------------------------
inline void NxTrace(PCWSTR format, ...) noexcept
{
    wchar_t buffer[1024];
    va_list args;
    va_start(args, format);
    int written = _snwprintf_s(buffer, _countof(buffer), _TRUNCATE, L"[NexusVCam %lu.%lu] ",
                               GetCurrentProcessId(), GetCurrentThreadId());
    if (written > 0)
    {
        _vsnwprintf_s(buffer + written, _countof(buffer) - written, _TRUNCATE, format, args);
    }
    va_end(args);
    OutputDebugStringW(buffer);
}

#define NX_RETURN_IF_FAILED(expr)                                        \
    do                                                                   \
    {                                                                    \
        const HRESULT _nxHr = (expr);                                    \
        if (FAILED(_nxHr))                                               \
        {                                                                \
            NxTrace(L"%hs(%d): hr=0x%08X %hs", __FILE__, __LINE__, _nxHr, #expr); \
            return _nxHr;                                                \
        }                                                                \
    } while (0)

#define NX_RETURN_HR_IF(hr, condition)                                   \
    do                                                                   \
    {                                                                    \
        if (condition)                                                   \
        {                                                                \
            NxTrace(L"%hs(%d): hr=0x%08X %hs", __FILE__, __LINE__, (HRESULT)(hr), #condition); \
            return (hr);                                                 \
        }                                                                \
    } while (0)

#define NX_RETURN_HR_IF_NULL(hr, ptr) NX_RETURN_HR_IF(hr, (ptr) == nullptr)

// ---------------------------------------------------------------------------
// DLL module lock. DllCanUnloadNow answers S_OK only when no live COM objects
// or factory locks remain; FrameServer keeps the DLL loaded while streaming.
// ---------------------------------------------------------------------------
extern std::atomic<long> g_moduleLock;

inline void NxModuleAddRef() noexcept { g_moduleLock.fetch_add(1, std::memory_order_relaxed); }
inline void NxModuleRelease() noexcept { g_moduleLock.fetch_sub(1, std::memory_order_relaxed); }

// ---------------------------------------------------------------------------
// Minimal smart pointer for COM interfaces.
// ---------------------------------------------------------------------------
template <typename T>
class ComPtr
{
public:
    ComPtr() noexcept = default;

    ComPtr(T* ptr) noexcept : _ptr(ptr)
    {
        if (_ptr) _ptr->AddRef();
    }

    ComPtr(const ComPtr& other) noexcept : ComPtr(other._ptr) {}

    ComPtr(ComPtr&& other) noexcept : _ptr(other._ptr) { other._ptr = nullptr; }

    ~ComPtr() { Reset(); }

    ComPtr& operator=(const ComPtr& other) noexcept
    {
        if (this != &other)
        {
            Reset();
            _ptr = other._ptr;
            if (_ptr) _ptr->AddRef();
        }
        return *this;
    }

    ComPtr& operator=(ComPtr&& other) noexcept
    {
        if (this != &other)
        {
            Reset();
            _ptr = other._ptr;
            other._ptr = nullptr;
        }
        return *this;
    }

    void Reset() noexcept
    {
        if (T* tmp = _ptr)
        {
            _ptr = nullptr;
            tmp->Release();
        }
    }

    T* Get() const noexcept { return _ptr; }
    T* operator->() const noexcept { return _ptr; }
    explicit operator bool() const noexcept { return _ptr != nullptr; }

    // For out-params of creation functions; releases any current pointer.
    T** Put() noexcept
    {
        Reset();
        return &_ptr;
    }

    void Attach(T* ptr) noexcept
    {
        Reset();
        _ptr = ptr;
    }

    T* Detach() noexcept
    {
        T* tmp = _ptr;
        _ptr = nullptr;
        return tmp;
    }

    HRESULT CopyTo(T** out) const noexcept
    {
        if (!out) return E_POINTER;
        *out = _ptr;
        if (_ptr) _ptr->AddRef();
        return S_OK;
    }

    template <typename U>
    HRESULT As(U** out) const noexcept
    {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (!_ptr) return E_POINTER;
        return _ptr->QueryInterface(__uuidof(U), reinterpret_cast<void**>(out));
    }

private:
    T* _ptr = nullptr;
};

// ---------------------------------------------------------------------------
// Slim lock wrappers.
// ---------------------------------------------------------------------------
class SrwLock
{
public:
    SrwLock() noexcept { InitializeSRWLock(&_lock); }
    SrwLock(const SrwLock&) = delete;
    SrwLock& operator=(const SrwLock&) = delete;

    void Acquire() noexcept { AcquireSRWLockExclusive(&_lock); }
    void Release() noexcept { ReleaseSRWLockExclusive(&_lock); }

private:
    SRWLOCK _lock;
};

class SrwGuard
{
public:
    explicit SrwGuard(SrwLock& lock) noexcept : _lock(lock) { _lock.Acquire(); }
    ~SrwGuard() { _lock.Release(); }
    SrwGuard(const SrwGuard&) = delete;
    SrwGuard& operator=(const SrwGuard&) = delete;

private:
    SrwLock& _lock;
};
