using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Single-flight, invalidation, TTL, and failure semantics of
/// <see cref="CachingUsbEnumerator"/>. HardwarePresence gates every
/// heartbeat worker off this cache, so a wrong verdict here stops device
/// connection attempts entirely.
/// </summary>
public class CachingUsbEnumeratorTests
{
    private sealed class FakeEnumerator : IUsbEnumerator
    {
        public int Calls;
        public Func<int, List<UsbDeviceEntry>>? OnEnumerate;
        public SemaphoreSlim? EnterGate;
        public SemaphoreSlim? ExitGate;

        public List<UsbDeviceEntry> Enumerate()
        {
            var call = Interlocked.Increment(ref Calls);
            EnterGate?.Release();
            ExitGate?.Wait(TimeSpan.FromSeconds(10));
            return OnEnumerate?.Invoke(call) ?? new List<UsbDeviceEntry>();
        }
    }

    private static UsbDeviceEntry Device(int vid, int pid)
        => new() { VendorId = vid, ProductId = pid, Name = $"dev-{vid:X4}" };

    [Fact]
    public void Fresh_cache_serves_without_rescanning()
    {
        var inner = new FakeEnumerator { OnEnumerate = _ => new List<UsbDeviceEntry> { Device(1, 2) } };
        var cache = new CachingUsbEnumerator(inner);

        var first = cache.Enumerate();
        var second = cache.Enumerate();

        Assert.Equal(1, inner.Calls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Concurrent_expired_callers_single_flight_one_scan()
    {
        var inner = new FakeEnumerator
        {
            EnterGate = new SemaphoreSlim(0),
            ExitGate = new SemaphoreSlim(0),
            OnEnumerate = _ => new List<UsbDeviceEntry> { Device(1, 2) },
        };
        var cache = new CachingUsbEnumerator(inner);

        var tasks = new Task<List<UsbDeviceEntry>>[4];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() => cache.Enumerate());
        }

        // One caller reaches the inner enumerator and blocks; release it once
        // it is inside, then let all callers finish.
        Assert.True(await inner.EnterGate.WaitAsync(TimeSpan.FromSeconds(10)));
        inner.ExitGate.Release(tasks.Length);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, inner.Calls);
        foreach (var t in tasks)
        {
            Assert.Single(await t);
        }
    }

    [Fact]
    public void Invalidate_forces_rescan()
    {
        var inner = new FakeEnumerator { OnEnumerate = c => new List<UsbDeviceEntry> { Device(c, c) } };
        var cache = new CachingUsbEnumerator(inner);

        var first = cache.Enumerate();
        cache.Invalidate();
        var second = cache.Enumerate();

        Assert.Equal(2, inner.Calls);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Invalidate_during_scan_leaves_cache_stale()
    {
        var inner = new FakeEnumerator
        {
            EnterGate = new SemaphoreSlim(0),
            ExitGate = new SemaphoreSlim(0),
            OnEnumerate = c => new List<UsbDeviceEntry> { Device(c, c) },
        };
        var cache = new CachingUsbEnumerator(inner);

        var scan = Task.Run(() => cache.Enumerate());
        Assert.True(await inner.EnterGate.WaitAsync(TimeSpan.FromSeconds(10)));

        // The bus changed while the scan was in flight: its result must not
        // be trusted as fresh.
        cache.Invalidate();
        inner.ExitGate.Release(2);
        await scan.WaitAsync(TimeSpan.FromSeconds(10));

        cache.Enumerate();
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Failed_refresh_serves_last_known_list_and_stays_stale()
    {
        var calls = 0;
        var inner = new FakeEnumerator
        {
            OnEnumerate = c =>
            {
                calls = c;
                if (c == 2) throw new InvalidOperationException("enumeration broken");
                return new List<UsbDeviceEntry> { Device(c, c) };
            },
        };
        var cache = new CachingUsbEnumerator(inner);

        var first = cache.Enumerate();
        cache.Invalidate();

        var duringFailure = cache.Enumerate();
        Assert.Equal(2, calls);
        Assert.Same(first, duringFailure); // last known-good list, not empty

        var recovered = cache.Enumerate(); // stale cache retries immediately
        Assert.Equal(3, calls);
        Assert.Equal(3, recovered[0].VendorId);
    }

    [Fact]
    public void SetTtl_zero_forces_rescan_every_call()
    {
        var inner = new FakeEnumerator { OnEnumerate = c => new List<UsbDeviceEntry> { Device(c, c) } };
        var cache = new CachingUsbEnumerator(inner);
        cache.SetTtl(TimeSpan.Zero);

        cache.Enumerate();
        cache.Enumerate();

        Assert.Equal(2, inner.Calls);
    }
}
