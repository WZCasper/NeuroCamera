using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroCamera.Engine;

/// <summary>
/// Minimal client for the obs-websocket v5 protocol (built into OBS 28+ by default). In OBS,
/// find the connection details under Инструменты (Tools) -&gt; Настройки WebSocket-сервера
/// (WebSocket Server Settings) -&gt; Показать сведения о подключении (Show Connect Info): that
/// dialog's "IP сервера" is this client's host, "Порт сервера" is the port, and "Пароль
/// сервера" is the password (only needed if authentication is enabled, which it is by
/// default).
///
/// Implements the real protocol handshake (Hello -&gt; Identify -&gt; Identified), including the
/// SHA256-based challenge/salt authentication OBS uses, and the SetSourceFilterSettings /
/// CreateSourceFilter requests used to push <see cref="ObsEquivalentCalculator"/>'s computed
/// values into a "Color Correction" filter - not a simplified stand-in.
/// </summary>
public static class ObsWebSocketClient
{
    /// <summary>
    /// Connects and authenticates only, without changing anything in OBS - lets the person
    /// verify host/port/password are correct before trusting <see cref="ApplyColorCorrectionAsync"/>
    /// to actually change a filter.
    /// </summary>
    public static async Task<(bool Success, string Message)> TestConnectionAsync(
        string host, int port, string password, CancellationToken cancellationToken = default)
    {
        ClientWebSocket? ws = null;
        try
        {
            ws = await ConnectAndIdentifyAsync(host, port, password, cancellationToken).ConfigureAwait(false);
            return (true, "Подключение к OBS успешно установлено.");
        }
        catch (Exception ex)
        {
            return (false, DescribeConnectionFailure(ex));
        }
        finally
        {
            await CloseQuietlyAsync(ws, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Connects to OBS, authenticates if a password is configured, and applies the given
    /// Color Correction values to the named filter on the named source - updating it if the
    /// filter already exists, creating it (kind "color_filter") if not. Every failure path
    /// (can't connect, wrong password, source/filter problems) returns a specific, readable
    /// message instead of throwing, since a failed attempt here should never crash the app.
    /// </summary>
    public static async Task<(bool Success, string Message)> ApplyColorCorrectionAsync(
        string host,
        int port,
        string password,
        string sourceName,
        string filterName,
        ObsEquivalentCalculator.ObsColorCorrectionValues values,
        CancellationToken cancellationToken = default)
    {
        ClientWebSocket? ws = null;

        try
        {
            ws = await ConnectAndIdentifyAsync(host, port, password, cancellationToken).ConfigureAwait(false);

            var filterSettings = new Dictionary<string, object?>
            {
                ["gamma"] = values.Gamma,
                ["contrast"] = values.Contrast,
                ["brightness"] = values.Brightness,
                ["saturation"] = values.Saturation,
                ["hue_shift"] = values.HueShift,
                ["opacity"] = values.Opacity
            };

            (bool setOk, string setMessage) = await SendRequestAsync(ws, "SetSourceFilterSettings", new Dictionary<string, object?>
            {
                ["sourceName"] = sourceName,
                ["filterName"] = filterName,
                ["filterSettings"] = filterSettings,
                ["overlay"] = false
            }, cancellationToken).ConfigureAwait(false);

            if (setOk)
            {
                return (true, $"Настройки применены к фильтру «{filterName}» на источнике «{sourceName}» в OBS.");
            }

            // Most likely the filter doesn't exist on this source yet - create it instead.
            (bool createOk, string createMessage) = await SendRequestAsync(ws, "CreateSourceFilter", new Dictionary<string, object?>
            {
                ["sourceName"] = sourceName,
                ["filterName"] = filterName,
                ["filterKind"] = "color_filter",
                ["filterSettings"] = filterSettings
            }, cancellationToken).ConfigureAwait(false);

            return createOk
                ? (true, $"Фильтр «{filterName}» создан и настроен на источнике «{sourceName}» в OBS.")
                : (false, $"OBS отклонил запрос. Обновление настроек: {setMessage}. Создание фильтра: {createMessage}. Проверьте точное имя источника в OBS.");
        }
        catch (Exception ex)
        {
            return (false, DescribeConnectionFailure(ex));
        }
        finally
        {
            await CloseQuietlyAsync(ws, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string DescribeConnectionFailure(Exception ex) =>
        $"Не удалось подключиться к OBS ({ex.Message}). Убедитесь, что в OBS включён WebSocket-сервер " +
        "(Инструменты → Настройки WebSocket-сервера → флажок \"Включить сервер WebSocket\") и что " +
        "хост/порт/пароль совпадают с тем, что показывает там же кнопка \"Показать сведения о подключении\".";

    /// <summary>Opens the socket and completes the Hello/Identify/Identified handshake, throwing on any failure.</summary>
    private static async Task<ClientWebSocket> ConnectAndIdentifyAsync(
        string host, int port, string password, CancellationToken cancellationToken)
    {
        var ws = new ClientWebSocket();
        var uri = new Uri($"ws://{host}:{port}");

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(8));
            await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
        }

        JsonDocument hello = await ReceiveJsonAsync(ws, cancellationToken).ConfigureAwait(false);
        JsonElement helloD = hello.RootElement.GetProperty("d");
        int rpcVersion = helloD.TryGetProperty("rpcVersion", out JsonElement rpcEl) ? rpcEl.GetInt32() : 1;

        string? authString = null;
        if (helloD.TryGetProperty("authentication", out JsonElement authEl))
        {
            string challenge = authEl.GetProperty("challenge").GetString() ?? "";
            string salt = authEl.GetProperty("salt").GetString() ?? "";
            authString = ComputeAuthString(password, salt, challenge);
        }

        var identifyData = new Dictionary<string, object?>
        {
            ["rpcVersion"] = rpcVersion,
            ["eventSubscriptions"] = 0
        };
        if (authString is not null)
        {
            identifyData["authentication"] = authString;
        }

        await SendJsonAsync(ws, new Dictionary<string, object?> { ["op"] = 1, ["d"] = identifyData }, cancellationToken).ConfigureAwait(false);

        JsonDocument identified = await ReceiveJsonAsync(ws, cancellationToken).ConfigureAwait(false);
        int op = identified.RootElement.GetProperty("op").GetInt32();
        if (op != 2)
        {
            throw new InvalidOperationException("OBS не подтвердил подключение — проверьте пароль WebSocket-сервера.");
        }

        return ws;
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket? ws, CancellationToken cancellationToken)
    {
        if (ws is null)
        {
            return;
        }

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort close - the connection is going away either way.
        }

        ws.Dispose();
    }

    private static string ComputeAuthString(string password, string salt, string challenge)
    {
        byte[] secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        string base64Secret = Convert.ToBase64String(secretHash);
        byte[] authHash = SHA256.HashData(Encoding.UTF8.GetBytes(base64Secret + challenge));
        return Convert.ToBase64String(authHash);
    }

    private static async Task<(bool Success, string Message)> SendRequestAsync(
        ClientWebSocket ws, string requestType, Dictionary<string, object?> requestData, CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        var request = new Dictionary<string, object?>
        {
            ["op"] = 6,
            ["d"] = new Dictionary<string, object?>
            {
                ["requestType"] = requestType,
                ["requestId"] = requestId,
                ["requestData"] = requestData
            }
        };

        await SendJsonAsync(ws, request, cancellationToken).ConfigureAwait(false);

        JsonDocument response = await ReceiveJsonAsync(ws, cancellationToken).ConfigureAwait(false);
        JsonElement d = response.RootElement.GetProperty("d");
        JsonElement status = d.GetProperty("requestStatus");
        bool result = status.GetProperty("result").GetBoolean();
        int code = status.TryGetProperty("code", out JsonElement codeEl) ? codeEl.GetInt32() : -1;
        string comment = status.TryGetProperty("comment", out JsonElement commentEl) ? (commentEl.GetString() ?? "") : "";

        return (result, result ? "OK" : $"код {code} {comment}".Trim());
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16384];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("OBS закрыл соединение до ответа.");
            }

            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
