using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NeuroCamera.Tests;

/// <summary>
/// A small in-process stand-in for OBS's WebSocket server that speaks the obs-websocket v5
/// protocol (Hello / Identify / Identified / RequestResponse) over a real socket, using only
/// the BCL. It keeps a tiny model of sources and their filters and answers the requests
/// <c>ObsWebSocketClient</c> makes with the same status codes real OBS uses (600 = not found,
/// 601 = already exists, 607 = invalid filter kind).
/// </summary>
internal sealed class FakeObsServer : IDisposable
{
    // Authentication example from the obs-websocket protocol documentation. The expected value
    // was computed independently (Python, following the documented steps: sha256(password+salt)
    // -> base64 -> + challenge -> sha256 -> base64), NOT by the code under test.
    public const string DocPassword = "supersecretpassword";
    public const string DocSalt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
    public const string DocChallenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";
    public const string DocExpectedAuthentication = "1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=";

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly object _sync = new();
    private readonly List<RecordedRequest> _requests = new();

    public FakeObsServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }

    /// <summary>When true the server demands the documented password/salt/challenge authentication.</summary>
    public bool RequirePassword { get; set; }

    /// <summary>Accept the WebSocket connection but never send anything (a hung / foreign service).</summary>
    public bool Silent { get; set; }

    /// <summary>Answer the handshake with plain text instead of an OBS Hello (a non-OBS service on the port).</summary>
    public bool SendGarbageInsteadOfHello { get; set; }

    /// <summary>Send an unrelated event message before every request response.</summary>
    public bool SendEventBeforeEachResponse { get; set; }

    /// <summary>Force a request type to fail with the given status code and comment.</summary>
    public Dictionary<string, (int Code, string Comment)> ForcedFailures { get; } = new();

    /// <summary>Sources known to the fake OBS and their filters (a source that is absent yields status 600).</summary>
    public Dictionary<string, List<FakeFilter>> Sources { get; } = new();

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToList();
            }
        }
    }

    public IReadOnlyList<string> RequestTypes => Requests.Select(r => r.Type).ToList();

    public RecordedRequest Single(string requestType) => Requests.Single(r => r.Type == requestType);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();

                string? key = await ReadHandshakeKeyAsync(stream, ct).ConfigureAwait(false);
                if (key is null)
                {
                    return;
                }

                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
                string response =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Upgrade: websocket\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);

                using WebSocket ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

                if (Silent)
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    return;
                }

                if (SendGarbageInsteadOfHello)
                {
                    await SendTextAsync(ws, "this is not obs-websocket", ct).ConfigureAwait(false);
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    return;
                }

                await RunProtocolAsync(ws, ct).ConfigureAwait(false);
            }
            catch
            {
                // Connection torn down by the client or by test shutdown.
            }
        }
    }

    private async Task RunProtocolAsync(WebSocket ws, CancellationToken ct)
    {
        Dictionary<string, object?> helloData = new()
        {
            ["obsWebSocketVersion"] = "5.5.0",
            ["rpcVersion"] = 1
        };
        if (RequirePassword)
        {
            helloData["authentication"] = new Dictionary<string, object?>
            {
                ["challenge"] = DocChallenge,
                ["salt"] = DocSalt
            };
        }

        await SendJsonAsync(ws, new Dictionary<string, object?> { ["op"] = 0, ["d"] = helloData }, ct).ConfigureAwait(false);

        using (JsonDocument identify = await ReceiveJsonAsync(ws, ct).ConfigureAwait(false))
        {
            JsonElement d = identify.RootElement.GetProperty("d");

            if (RequirePassword)
            {
                string? supplied = d.TryGetProperty("authentication", out JsonElement auth) ? auth.GetString() : null;
                if (supplied != DocExpectedAuthentication)
                {
                    await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", ct).ConfigureAwait(false);
                    return;
                }
            }
        }

        await SendJsonAsync(ws, new Dictionary<string, object?>
        {
            ["op"] = 2,
            ["d"] = new Dictionary<string, object?> { ["negotiatedRpcVersion"] = 1 }
        }, ct).ConfigureAwait(false);

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            JsonDocument message = await ReceiveJsonAsync(ws, ct).ConfigureAwait(false);
            using (message)
            {
                JsonElement root = message.RootElement;
                if (root.GetProperty("op").GetInt32() != 6)
                {
                    continue;
                }

                JsonElement d = root.GetProperty("d");
                string type = d.GetProperty("requestType").GetString()!;
                string id = d.GetProperty("requestId").GetString()!;
                JsonElement data = d.TryGetProperty("requestData", out JsonElement requestData) ? requestData.Clone() : default;

                lock (_sync)
                {
                    _requests.Add(new RecordedRequest(type, data));
                }

                (int code, string comment, object? responseData) = Handle(type, data);
                bool success = code == 100;

                if (SendEventBeforeEachResponse)
                {
                    await SendJsonAsync(ws, new Dictionary<string, object?>
                    {
                        ["op"] = 5,
                        ["d"] = new Dictionary<string, object?> { ["eventType"] = "SomethingHappened", ["eventIntent"] = 1 }
                    }, ct).ConfigureAwait(false);
                }

                Dictionary<string, object?> status = new() { ["result"] = success, ["code"] = code };
                if (!success)
                {
                    status["comment"] = comment;
                }

                Dictionary<string, object?> responseBody = new()
                {
                    ["requestType"] = type,
                    ["requestId"] = id,
                    ["requestStatus"] = status
                };
                if (success && responseData is not null)
                {
                    responseBody["responseData"] = responseData;
                }

                await SendJsonAsync(ws, new Dictionary<string, object?> { ["op"] = 7, ["d"] = responseBody }, ct).ConfigureAwait(false);
            }
        }
    }

    private (int Code, string Comment, object? ResponseData) Handle(string type, JsonElement data)
    {
        if (ForcedFailures.TryGetValue(type, out (int Code, string Comment) forced))
        {
            return (forced.Code, forced.Comment, null);
        }

        lock (_sync)
        {
            string sourceName = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sourceName", out JsonElement s) ? s.GetString() ?? "" : "";
            if (!Sources.TryGetValue(sourceName, out List<FakeFilter>? filters))
            {
                return (600, $"No source was found by the name of `{sourceName}`.", null);
            }

            string filterName = data.TryGetProperty("filterName", out JsonElement f) ? f.GetString() ?? "" : "";

            switch (type)
            {
                case "GetSourceFilterList":
                    return (100, "", new Dictionary<string, object?>
                    {
                        ["filters"] = filters.Select((flt, index) => new Dictionary<string, object?>
                        {
                            ["filterEnabled"] = true,
                            ["filterIndex"] = index,
                            ["filterKind"] = flt.Kind,
                            ["filterName"] = flt.Name,
                            ["filterSettings"] = new Dictionary<string, object?>()
                        }).ToList()
                    });

                case "CreateSourceFilter":
                {
                    if (filters.Any(x => x.Name == filterName))
                    {
                        return (601, "A filter already exists by that name.", null);
                    }

                    string kind = data.GetProperty("filterKind").GetString() ?? "";
                    if (kind is not ("color_filter_v2" or "color_filter" or "clut_filter"))
                    {
                        return (607, "Your specified filter kind is not supported by OBS.", null);
                    }

                    filters.Add(new FakeFilter(filterName, kind));
                    return (100, "", null);
                }

                case "RemoveSourceFilter":
                {
                    int removed = filters.RemoveAll(x => x.Name == filterName);
                    return removed > 0 ? (100, "", null) : (600, "No filter was found by that name.", null);
                }

                case "SetSourceFilterSettings":
                    return filters.Any(x => x.Name == filterName) ? (100, "", null) : (600, "No filter was found by that name.", null);

                case "SetSourceFilterIndex":
                {
                    FakeFilter? filter = filters.FirstOrDefault(x => x.Name == filterName);
                    if (filter is null)
                    {
                        return (600, "No filter was found by that name.", null);
                    }

                    int newIndex = Math.Clamp(data.GetProperty("filterIndex").GetInt32(), 0, filters.Count - 1);
                    filters.Remove(filter);
                    filters.Insert(newIndex, filter);
                    return (100, "", null);
                }

                default:
                    return (204, $"Unknown request type {type}.", null);
            }
        }
    }

    private static async Task<string?> ReadHandshakeKeyAsync(NetworkStream stream, CancellationToken ct)
    {
        StringBuilder header = new();
        byte[] one = new byte[1];

        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            header.Append((char)one[0]);
        }

        foreach (string line in header.ToString().Split("\r\n"))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                return line["Sec-WebSocket-Key:".Length..].Trim();
            }
        }

        return null;
    }

    private static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private static Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct) =>
        SendTextAsync(ws, JsonSerializer.Serialize(payload), ct);

    private static async Task<JsonDocument> ReceiveJsonAsync(WebSocket ws, CancellationToken ct)
    {
        byte[] buffer = new byte[16384];
        using MemoryStream ms = new();

        ValueWebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new OperationCanceledException("client closed");
            }

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms, cancellationToken: ct).ConfigureAwait(false);
    }

    internal sealed record FakeFilter(string Name, string Kind);

    internal sealed record RecordedRequest(string Type, JsonElement Data);
}
