using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Linux.DBus;

/// <summary>
/// A pure-C# D-Bus session-bus client. Handles SASL EXTERNAL auth, message
/// marshalling, method calls with reply matching, and dispatch of incoming
/// method calls to registered path handlers.
///
/// Shared across subsystems: one connection per process, multiple handlers
/// for different object paths (tray SNI, screen-time receiver, etc.).
/// </summary>
public sealed class DBusConnection : IDisposable
{
    private static int _instanceCounter;
    private readonly int _instanceId = Interlocked.Increment(ref _instanceCounter);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Socket? _socket;
    private NetworkStream? _stream;
    private uint _serial;
    private readonly object _sendLock = new();
    private readonly Dictionary<uint, TaskCompletionSource<DBusMessage>> _pending = new();
    private readonly Dictionary<string, Func<DBusMessage, DBusMessage?>> _handlers = new();
    private Task? _readerTask;
    private bool _started;

    public string UniqueName { get; private set; } = "";
    public bool Connected { get; private set; }

    /// <summary>Connect + authenticate + Hello. Idempotent; safe to call from multiple subsystems.</summary>
    public async Task StartAsync()
    {
        if (_started)
            return;
        await _startLock.WaitAsync();
        try
        {
            if (_started)
                return;
            var socketPath = ResolveSocketPath();
            if (!File.Exists(socketPath))
            {
                throw new InvalidOperationException($"D-Bus session bus socket not found at {socketPath}");
            }
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await _socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            _stream = new NetworkStream(_socket, ownsSocket: false);

            await AuthAsync();
            _readerTask = Task.Run(ReadLoopAsync);
            UniqueName = await HelloAsync();
            Connected = true;
            _started = true;
            Console.Error.WriteLine($"[dbus] connection started, unique={UniqueName} instance#{_instanceId}");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<DBusMessage> CallAsync(
        string destination,
        string path,
        string iface,
        string member,
        string signature,
        Action<DBusWriter>? body,
        int timeoutMs = 5000)
    {
        var serial = NextSerial();
        var msg = new DBusMessage
        {
            Type = DBusMessageType.MethodCall,
            Flags = 0,
            Serial = serial,
            Destination = destination,
            Path = path,
            Interface = iface,
            Member = member,
            Signature = signature,
        };
        if (body is not null)
        {
            var w = new DBusWriter();
            body(w);
            msg.Body = w.ToArray();
        }
        var tcs = new TaskCompletionSource<DBusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[serial] = tcs;
        }
        SendMessage(msg);
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var reg = timeout.Token.Register(() => tcs.TrySetException(new TimeoutException($"{iface}.{member} timed out")));
        return await tcs.Task;
    }

    public async Task<uint> RequestNameAsync(string name, uint flags = 0)
    {
        var reply = await CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "RequestName", "su", w =>
            {
                w.WriteString(name);
                w.WriteUInt32(flags);
            });
        return new DBusReader(reply.Body).ReadUInt32();
    }

    /// <summary>Register a handler for incoming method calls on a given object path.</summary>
    public void RegisterHandler(string path, Func<DBusMessage, DBusMessage?> handler)
    {
        lock (_handlers)
        {
            _handlers[path] = handler;
        }
    }

    public void UnregisterHandler(string path)
    {
        lock (_handlers)
        {
            _handlers.Remove(path);
        }
    }

    internal void SendMessage(DBusMessage msg)
    {
        var bytes = msg.Encode();
        lock (_sendLock)
        {
            _stream!.Write(bytes, 0, bytes.Length);
        }
    }

    internal uint NextSerial()
    {
        var s = Interlocked.Increment(ref _serial);
        return s == 0 ? Interlocked.Increment(ref _serial) : s;
    }

    public void Dispose()
    {
        _cts.Cancel();
        Connected = false;
        try
        { _stream?.Dispose(); }
        catch { }
        try
        { _socket?.Dispose(); }
        catch { }
    }

    private static string ResolveSocketPath()
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrEmpty(address))
        {
            const string prefix = "unix:path=";
            var idx = address.IndexOf(prefix, StringComparison.Ordinal);
            if (idx >= 0)
            {
                return address[(idx + prefix.Length)..].Split(',')[0];
            }
        }
        return $"/run/user/{GetUid()}/bus";
    }

    private static int GetUid()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var uid))
                    {
                        return uid;
                    }
                }
            }
        }
        catch { }
        return 1000;
    }

    private async Task AuthAsync()
    {
        _stream!.WriteByte(0);
        var uidHex = ToHex(GetUid().ToString());
        await WriteLineAsync($"AUTH EXTERNAL {uidHex}");
        var line = await ReadLineAsync();
        if (!line.StartsWith("OK ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"D-Bus auth failed: {line}");
        }
        await WriteLineAsync("BEGIN");
    }

    private static string ToHex(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private async Task WriteLineAsync(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await _stream!.WriteAsync(bytes);
    }

    private async Task<string> ReadLineAsync()
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await _stream!.ReadAsync(buf.AsMemory(0, 1));
            if (n == 0)
                break;
            var c = (char)buf[0];
            if (c == '\r')
                continue;
            if (c == '\n')
                break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task<string> HelloAsync()
    {
        var reply = await CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "Hello", "", null);
        return new DBusReader(reply.Body).ReadString();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await DBusMessage.ReadAsync(_stream!, _cts.Token);
                if (msg is null)
                {
                    Console.Error.WriteLine("[dbus] bus connection closed (EOF)");
                    Connected = false;
                    break;
                }
                HandleIncoming(msg);
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[dbus] reader loop exited: {ex.Message}");
                Connected = false;
            }
        }
    }

    private void HandleIncoming(DBusMessage msg)
    {
        if (msg.Type == DBusMessageType.MethodReturn || msg.Type == DBusMessageType.Error)
        {
            TaskCompletionSource<DBusMessage>? tcs = null;
            lock (_pending)
            {
                if (msg.ReplySerial != 0 && _pending.TryGetValue(msg.ReplySerial, out tcs))
                {
                    _pending.Remove(msg.ReplySerial);
                }
            }
            if (tcs is not null)
            {
                if (msg.Type == DBusMessageType.Error)
                {
                    tcs.TrySetException(new InvalidOperationException(
                        $"D-Bus error {msg.ErrorName}: {TryReadErrorMessage(msg.Body)}"));
                }
                else
                {
                    tcs.TrySetResult(msg);
                }
            }
            return;
        }

        if (msg.Type == DBusMessageType.MethodCall)
        {
            _ = Task.Run(() => DispatchMethodCall(msg));
        }
    }

    private void DispatchMethodCall(DBusMessage msg)
    {
        try
        {
            Func<DBusMessage, DBusMessage?>? handler = null;
            lock (_handlers)
            {
                if (msg.Path is not null)
                {
                    _handlers.TryGetValue(msg.Path, out handler);
                }
            }

            Console.Error.WriteLine($"[dbus] call {msg.Path} {msg.Interface}.{msg.Member} from {msg.Sender} handler={(handler != null)}");

            DBusMessage? reply;
            if (handler is null)
            {
                reply = MakeErrorReply(msg, "org.freedesktop.DBus.Error.UnknownObject",
                    $"no handler for {msg.Path}");
            }
            else
            {
                reply = handler(msg);
            }

            if (reply is not null)
            {
                SendMessage(reply);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[dbus] handler {msg.Interface}.{msg.Member} failed: {ex.Message}");
            SendMessage(MakeErrorReply(msg, "org.freedesktop.DBus.Error.Failed", ex.Message));
        }
    }

    private static string TryReadErrorMessage(byte[] body)
    {
        if (body.Length == 0)
            return "";
        try
        { return new DBusReader(body).ReadString(); }
        catch { return ""; }
    }

    public DBusMessage MakeReply(DBusMessage call, string signature, Action<DBusWriter>? body)
    {
        var reply = new DBusMessage
        {
            Type = DBusMessageType.MethodReturn,
            Flags = 1,
            Serial = NextSerial(),
            ReplySerial = call.Serial,
            Destination = call.Sender,
            Signature = signature,
        };
        if (body is not null && signature.Length > 0)
        {
            var w = new DBusWriter();
            body(w);
            reply.Body = w.ToArray();
        }
        return reply;
    }

    public DBusMessage MakeErrorReply(DBusMessage call, string errorName, string message)
    {
        var reply = new DBusMessage
        {
            Type = DBusMessageType.Error,
            Flags = 1,
            Serial = NextSerial(),
            ReplySerial = call.Serial,
            Destination = call.Sender,
            ErrorName = errorName,
            Signature = "s",
        };
        var w = new DBusWriter();
        w.WriteString(message);
        reply.Body = w.ToArray();
        return reply;
    }
}
