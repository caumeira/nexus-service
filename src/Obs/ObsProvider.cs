using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Qos.Service.Models;
using Qos.Service.Models.Obs;
using Qos.Service.Persistence;

namespace Qos.Service.Obs;

public sealed class ObsProvider : IObsProvider, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2.5);

    private readonly IConfigStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientWebSocket? _socket;

    public ObsProvider(IConfigStore store)
    {
        _store = store;
    }

    public ObsConfigResponse GetConfig()
    {
        var settings = _store.Load().Obs;
        return new ObsConfigResponse
        {
            Host = NormalizeHostForDisplay(settings.Host),
            Port = NormalizePort(settings.Port),
            HasPassword = !string.IsNullOrEmpty(settings.Password),
        };
    }

    public void SetConfig(ObsConfigBody body)
    {
        _store.Update(s =>
        {
            if (body.Host is not null)
            {
                s.Obs.Host = string.IsNullOrWhiteSpace(body.Host) ? "127.0.0.1" : body.Host.Trim();
            }
            if (body.Port is > 0 and < 65536)
            {
                s.Obs.Port = body.Port.Value;
            }
            if (body.Password is not null)
            {
                s.Obs.Password = body.Password;
            }
        });

        _gate.Wait();
        try
        {
            ResetSocketLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ObsStatusResponse> GetStatusAsync(CancellationToken cancellationToken) =>
        RunWithSocketAsync(BuildStatusLockedAsync, cancellationToken);

    public Task<ObsStatusResponse> ToggleRecordingAsync(CancellationToken cancellationToken) =>
        RunWithSocketAsync(async ct =>
        {
            await SendRequestLockedAsync("ToggleRecord", null, ct).ConfigureAwait(false);
            return await BuildStatusLockedAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<ObsStatusResponse> ToggleStreamingAsync(CancellationToken cancellationToken) =>
        RunWithSocketAsync(async ct =>
        {
            await SendRequestLockedAsync("ToggleStream", null, ct).ConfigureAwait(false);
            return await BuildStatusLockedAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<ObsStatusResponse> SetSceneAsync(string sceneName, CancellationToken cancellationToken) =>
        RunWithSocketAsync(async ct =>
        {
            var trimmed = sceneName.Trim();
            if (trimmed.Length == 0)
            {
                return Disconnected("Scene name is required");
            }

            await SendRequestLockedAsync("SetCurrentProgramScene", writer =>
            {
                writer.WriteString("sceneName", trimmed);
            }, ct).ConfigureAwait(false);
            return await BuildStatusLockedAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public ApiResponse Launch()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return LaunchWindows();
            }
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open")
                {
                    UseShellExecute = false,
                    ArgumentList = { "-a", "OBS" },
                });
                return ApiResponse.Ok("launched");
            }

            Process.Start(new ProcessStartInfo("obs")
            {
                UseShellExecute = true,
            });
            return ApiResponse.Ok("launched");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to launch OBS: {ex.Message}", ex);
        }
    }

    private static ApiResponse LaunchWindows()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "obs-studio", "bin", "64bit", "obs64.exe"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            Process.Start(new ProcessStartInfo(path)
            {
                WorkingDirectory = Path.GetDirectoryName(path),
                UseShellExecute = true,
            });
            return ApiResponse.Ok("launched");
        }

        Process.Start(new ProcessStartInfo("obs64.exe")
        {
            UseShellExecute = true,
        });
        return ApiResponse.Ok("launched");
    }

    private async Task<ObsStatusResponse> RunWithSocketAsync(
        Func<CancellationToken, Task<ObsStatusResponse>> action,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        var acquired = false;
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            return await action(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (acquired)
            {
                ResetSocketLocked();
            }
            return Disconnected(ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (acquired)
            {
                ResetSocketLocked();
            }
            return Disconnected("OBS did not respond in time");
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    private async Task<ObsStatusResponse> BuildStatusLockedAsync(CancellationToken cancellationToken)
    {
        var sceneData = await SendRequestLockedAsync("GetSceneList", null, cancellationToken).ConfigureAwait(false);
        var recordData = await SendRequestLockedAsync("GetRecordStatus", null, cancellationToken).ConfigureAwait(false);
        var streamData = await SendRequestLockedAsync("GetStreamStatus", null, cancellationToken).ConfigureAwait(false);
        var settings = _store.Load().Obs;

        return new ObsStatusResponse
        {
            Connected = true,
            Host = NormalizeHostForDisplay(settings.Host),
            Port = NormalizePort(settings.Port),
            ActiveScene = ReadString(sceneData, "currentProgramSceneName"),
            Scenes = ReadScenes(sceneData),
            Recording = ReadBool(recordData, "outputActive"),
            Streaming = ReadBool(streamData, "outputActive"),
            RecordingDurationMs = ReadLong(recordData, "outputDuration"),
            StreamingDurationMs = ReadLong(streamData, "outputDuration"),
        };
    }

    private async Task<JsonElement> SendRequestLockedAsync(
        string requestType,
        Action<Utf8JsonWriter>? writeRequestData,
        CancellationToken cancellationToken)
    {
        var socket = await EnsureConnectedLockedAsync(cancellationToken).ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");
        await SendObsRequestAsync(socket, requestType, requestId, writeRequestData, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            using var doc = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opEl))
            {
                continue;
            }

            var op = opEl.GetInt32();
            if (op != 7)
            {
                continue;
            }

            var d = root.GetProperty("d");
            if (ReadString(d, "requestId") != requestId)
            {
                continue;
            }

            var status = d.GetProperty("requestStatus");
            if (!ReadBool(status, "result"))
            {
                var message = ReadString(status, "comment");
                if (message.Length == 0)
                {
                    message = $"{requestType} failed";
                }
                throw new InvalidOperationException(message);
            }

            return d.TryGetProperty("responseData", out var data)
                ? data.Clone()
                : default;
        }
    }

    private async Task<ClientWebSocket> EnsureConnectedLockedAsync(CancellationToken cancellationToken)
    {
        if (_socket is { State: WebSocketState.Open })
        {
            return _socket;
        }

        ResetSocketLocked();

        var settings = _store.Load().Obs;
        var endpoint = NormalizeEndpoint(settings.Host, settings.Port);
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint.Uri, cancellationToken).ConfigureAwait(false);

            using var hello = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
            var helloRoot = hello.RootElement;
            if (ReadInt(helloRoot, "op") != 0)
            {
                throw new InvalidOperationException("OBS sent an unexpected handshake");
            }

            var helloData = helloRoot.GetProperty("d");
            var auth = "";
            if (helloData.TryGetProperty("authentication", out var authData))
            {
                if (string.IsNullOrEmpty(settings.Password))
                {
                    throw new InvalidOperationException("OBS password is required");
                }

                auth = ObsProtocol.ComputeAuthentication(
                    settings.Password,
                    ReadString(authData, "salt"),
                    ReadString(authData, "challenge"));
            }

            await SendIdentifyAsync(socket, auth, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                using var identified = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
                var op = ReadInt(identified.RootElement, "op");
                if (op == 2)
                {
                    _socket = socket;
                    return socket;
                }
                if (op == 9)
                {
                    throw new InvalidOperationException("OBS rejected the connection");
                }
            }
        }
        catch
        {
            socket.Abort();
            socket.Dispose();
            throw;
        }
    }

    private static async Task SendIdentifyAsync(ClientWebSocket socket, string authentication, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("op", 1);
            writer.WriteStartObject("d");
            writer.WriteNumber("rpcVersion", 1);
            if (authentication.Length > 0)
            {
                writer.WriteString("authentication", authentication);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await socket.SendAsync(stream.ToArray(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendObsRequestAsync(
        ClientWebSocket socket,
        string requestType,
        string requestId,
        Action<Utf8JsonWriter>? writeRequestData,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("op", 6);
            writer.WriteStartObject("d");
            writer.WriteString("requestType", requestType);
            writer.WriteString("requestId", requestId);
            if (writeRequestData is not null)
            {
                writer.WritePropertyName("requestData");
                writer.WriteStartObject();
                writeRequestData(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await socket.SendAsync(stream.ToArray(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("OBS closed the connection");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(stream.ToArray());
            }
        }
    }

    private void ResetSocketLocked()
    {
        try
        {
            _socket?.Abort();
            _socket?.Dispose();
        }
        catch
        {
            // Best-effort cleanup only.
        }
        _socket = null;
    }

    private ObsStatusResponse Disconnected(string message)
    {
        var settings = _store.Load().Obs;
        return new ObsStatusResponse
        {
            Error = true,
            Msg = message,
            Connected = false,
            Host = NormalizeHostForDisplay(settings.Host),
            Port = NormalizePort(settings.Port),
        };
    }

    private static List<ObsScene> ReadScenes(JsonElement data)
    {
        var scenes = new List<ObsScene>();
        if (!data.TryGetProperty("scenes", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return scenes;
        }

        foreach (var item in array.EnumerateArray())
        {
            var name = ReadString(item, "sceneName");
            if (name.Length == 0)
            {
                continue;
            }

            scenes.Add(new ObsScene
            {
                Name = name,
                Uuid = ReadString(item, "sceneUuid"),
            });
        }
        return scenes;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.True;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static long ReadLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return 0;
        }

        if (property.TryGetInt64(out var value))
        {
            return value;
        }
        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value))
        {
            return value;
        }
        return 0;
    }

    private static int NormalizePort(int port) => port > 0 && port < 65536 ? port : 4455;

    private static string NormalizeHostForDisplay(string host)
    {
        var endpoint = NormalizeEndpoint(host, 4455);
        return endpoint.Host;
    }

    private static (Uri Uri, string Host) NormalizeEndpoint(string host, int port)
    {
        var trimmed = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == "ws" || absolute.Scheme == "wss" || absolute.Scheme == "http" || absolute.Scheme == "https"))
        {
            var scheme = absolute.Scheme is "wss" or "https" ? "wss" : "ws";
            var uriPort = absolute.IsDefaultPort ? NormalizePort(port) : absolute.Port;
            return (new UriBuilder(scheme, absolute.Host, uriPort).Uri, absolute.Host);
        }

        var slash = trimmed.IndexOf('/');
        if (slash >= 0)
        {
            trimmed = trimmed[..slash];
        }

        var hostPart = trimmed;
        var portPart = NormalizePort(port);
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && int.TryParse(trimmed[(colon + 1)..], out var parsedPort))
        {
            hostPart = trimmed[..colon];
            portPart = NormalizePort(parsedPort);
        }

        hostPart = string.IsNullOrWhiteSpace(hostPart) ? "127.0.0.1" : hostPart;
        return (new UriBuilder("ws", hostPart, portPart).Uri, hostPart);
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            ResetSocketLocked();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
