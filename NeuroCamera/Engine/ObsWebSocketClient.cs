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
/// SHA256-based challenge/salt authentication OBS uses, and pushes
/// <see cref="ObsEquivalentCalculator"/>'s values into a "Color Correction" filter.
///
/// Facts about OBS this client is built around (taken from the obs-studio / obs-websocket
/// sources, not guessed):
///  - The current Color Correction filter is registered as <c>color_filter_v2</c>. The plain id
///    <c>color_filter</c> is the obsolete v1 filter (flagged OBS_SOURCE_CAP_OBSOLETE) whose
///    contrast/brightness maths and 0-100 opacity differ from the values this app computes, so
///    new filters are always created as v2 and an existing v1 filter is replaced.
///  - A wrong password is not reported in a message: OBS closes the socket with code 4009.
///  - RequestStatus 600 means "resource not found" (here: the source does not exist).
///
/// No call ever throws to the caller and none can hang: every operation has an overall
/// timeout, and every failure is turned into a specific, readable message.
/// </summary>
public static class ObsWebSocketClient
{
    /// <summary>OBS's current "Color Correction" filter (versioned id of <c>color_filter</c> version 2).</summary>
    internal const string ColorFilterKind = "color_filter_v2";

    /// <summary>The obsolete first version of the Color Correction filter.</summary>
    internal const string LegacyColorFilterKind = "color_filter";

    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(15);

    private const int RpcVersion = 1;
    private const int StatusResourceNotFound = 600;
    private const int CloseCodeAuthenticationFailed = 4009;
    private const int CloseCodeUnsupportedRpcVersion = 4010;
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Connects and authenticates only, without changing anything in OBS - lets the person
    /// verify host/port/password are correct before trusting <see cref="ApplyColorCorrectionAsync(string, int, string, string, string, ObsEquivalentCalculator.ObsColorCorrectionValues, CancellationToken)"/>
    /// to actually change a filter.
    /// </summary>
    public static Task<(bool Success, string Message)> TestConnectionAsync(
        string host, int port, string password, CancellationToken cancellationToken = default) =>
        TestConnectionAsync(host, port, password, DefaultOperationTimeout, cancellationToken);

    internal static async Task<(bool Success, string Message)> TestConnectionAsync(
        string host, int port, string password, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (ValidateEndpoint(host, port) is { } endpointProblem)
        {
            return (false, endpointProblem);
        }

        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(timeout);

        ClientWebSocket? ws = null;
        try
        {
            ws = await ConnectAndIdentifyAsync(host, port, password, operation.Token).ConfigureAwait(false);
            return (true, "Подключение к OBS успешно установлено.");
        }
        catch (Exception ex)
        {
            return (false, DescribeFailure(ex, cancellationToken));
        }
        finally
        {
            await CloseQuietlyAsync(ws).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Connects to OBS, authenticates if a password is configured, and applies the given
    /// Color Correction values to the named filter on the named source. Looks the source's
    /// filters up first so it can do exactly the right thing: update an existing current-version
    /// filter, create a missing one (as <c>color_filter_v2</c>), replace an obsolete v1 filter of
    /// the same name, or refuse to touch a filter of some unrelated kind. Every failure path
    /// (can't connect, wrong password, no such source, ...) returns a specific, readable
    /// message instead of throwing, since a failed attempt here should never crash the app.
    /// </summary>
    public static Task<(bool Success, string Message)> ApplyColorCorrectionAsync(
        string host,
        int port,
        string password,
        string sourceName,
        string filterName,
        ObsEquivalentCalculator.ObsColorCorrectionValues values,
        CancellationToken cancellationToken = default) =>
        ApplyColorCorrectionAsync(host, port, password, sourceName, filterName, values, DefaultOperationTimeout, cancellationToken);

    internal static async Task<(bool Success, string Message)> ApplyColorCorrectionAsync(
        string host,
        int port,
        string password,
        string sourceName,
        string filterName,
        ObsEquivalentCalculator.ObsColorCorrectionValues values,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (ValidateEndpoint(host, port) is { } endpointProblem)
        {
            return (false, endpointProblem);
        }

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return (false, "Укажите точное имя источника камеры так, как оно называется в списке источников OBS.");
        }

        if (string.IsNullOrWhiteSpace(filterName))
        {
            return (false, "Укажите имя фильтра.");
        }

        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(timeout);

        ClientWebSocket? ws = null;
        try
        {
            ws = await ConnectAndIdentifyAsync(host, port, password, operation.Token).ConfigureAwait(false);
            return await ApplyToConnectedObsAsync(ws, sourceName, filterName, values, operation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, DescribeFailure(ex, cancellationToken));
        }
        finally
        {
            await CloseQuietlyAsync(ws).ConfigureAwait(false);
        }
    }

    private static async Task<(bool Success, string Message)> ApplyToConnectedObsAsync(
        ClientWebSocket ws,
        string sourceName,
        string filterName,
        ObsEquivalentCalculator.ObsColorCorrectionValues values,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> filterSettings = BuildFilterSettings(values);

        ObsResponse<IReadOnlyList<ObsFilterInfo>> listResponse = await SendRequestAsync(
            ws, "GetSourceFilterList", new Dictionary<string, object?> { ["sourceName"] = sourceName },
            ParseFilterList, cancellationToken).ConfigureAwait(false);

        if (!listResponse.Success)
        {
            return listResponse.Code == StatusResourceNotFound
                ? (false, $"Источник «{sourceName}» не найден в OBS. Укажите имя источника точно так, как оно написано в списке источников OBS (регистр важен).")
                : (false, $"OBS отклонил запрос списка фильтров источника «{sourceName}»: {Describe(listResponse)}.");
        }

        ObsFilterInfo? existing = (listResponse.Data ?? Array.Empty<ObsFilterInfo>())
            .FirstOrDefault(filter => string.Equals(filter.Name, filterName, StringComparison.Ordinal));

        if (existing is null)
        {
            ObsResponse<bool> created = await CreateFilterAsync(ws, sourceName, filterName, filterSettings, cancellationToken).ConfigureAwait(false);
            return created.Success
                ? (true, $"Фильтр «{filterName}» создан и настроен на источнике «{sourceName}» в OBS.")
                : (false, $"OBS не смог создать фильтр «{filterName}»: {Describe(created)}.");
        }

        if (existing.Kind == ColorFilterKind)
        {
            ObsResponse<bool> updated = await SendRequestAsync(ws, "SetSourceFilterSettings", new Dictionary<string, object?>
            {
                ["sourceName"] = sourceName,
                ["filterName"] = filterName,
                ["filterSettings"] = filterSettings,
                ["overlay"] = false
            }, cancellationToken).ConfigureAwait(false);

            return updated.Success
                ? (true, $"Настройки применены к фильтру «{filterName}» на источнике «{sourceName}» в OBS.")
                : (false, $"OBS отклонил обновление фильтра «{filterName}»: {Describe(updated)}.");
        }

        if (existing.Kind == LegacyColorFilterKind)
        {
            return await ReplaceLegacyFilterAsync(ws, sourceName, filterName, existing, filterSettings, cancellationToken).ConfigureAwait(false);
        }

        return (false,
            $"На источнике «{sourceName}» уже есть фильтр «{filterName}», но это фильтр другого типа ({existing.Kind}). " +
            "Укажите другое имя фильтра — приложение не меняет чужие фильтры.");
    }

    /// <summary>
    /// The obsolete v1 Color Correction filter interprets the numbers this app computes
    /// differently (contrast mapping, brightness order, opacity scale), so it cannot simply be
    /// updated. It is replaced by a current-version filter with the same name and position.
    /// </summary>
    private static async Task<(bool Success, string Message)> ReplaceLegacyFilterAsync(
        ClientWebSocket ws,
        string sourceName,
        string filterName,
        ObsFilterInfo legacy,
        Dictionary<string, object?> filterSettings,
        CancellationToken cancellationToken)
    {
        ObsResponse<bool> removed = await SendRequestAsync(ws, "RemoveSourceFilter", new Dictionary<string, object?>
        {
            ["sourceName"] = sourceName,
            ["filterName"] = filterName
        }, cancellationToken).ConfigureAwait(false);

        if (!removed.Success)
        {
            return (false, $"Фильтр «{filterName}» устаревшего типа, и заменить его не удалось: OBS отклонил удаление ({Describe(removed)}).");
        }

        ObsResponse<bool> created = await CreateFilterAsync(ws, sourceName, filterName, filterSettings, cancellationToken).ConfigureAwait(false);
        if (!created.Success)
        {
            return (false,
                $"Устаревший фильтр «{filterName}» удалён, но создать новый не удалось: {Describe(created)}. " +
                "Повторите попытку или добавьте фильтр «Цветокоррекция» в OBS вручную.");
        }

        // Best effort: put the replacement where the old filter was in the filter stack.
        await SendRequestAsync(ws, "SetSourceFilterIndex", new Dictionary<string, object?>
        {
            ["sourceName"] = sourceName,
            ["filterName"] = filterName,
            ["filterIndex"] = legacy.Index
        }, cancellationToken).ConfigureAwait(false);

        return (true,
            $"Фильтр «{filterName}» был устаревшего типа (OBS v1) — он заменён актуальным и настроен на источнике «{sourceName}».");
    }

    private static Task<ObsResponse<bool>> CreateFilterAsync(
        ClientWebSocket ws, string sourceName, string filterName, Dictionary<string, object?> filterSettings, CancellationToken cancellationToken) =>
        SendRequestAsync(ws, "CreateSourceFilter", new Dictionary<string, object?>
        {
            ["sourceName"] = sourceName,
            ["filterName"] = filterName,
            ["filterKind"] = ColorFilterKind,
            ["filterSettings"] = filterSettings
        }, cancellationToken);

    private static Dictionary<string, object?> BuildFilterSettings(ObsEquivalentCalculator.ObsColorCorrectionValues values) => new()
    {
        ["gamma"] = values.Gamma,
        ["contrast"] = values.Contrast,
        ["brightness"] = values.Brightness,
        ["saturation"] = values.Saturation,
        ["hue_shift"] = values.HueShift,
        ["opacity"] = values.Opacity
    };

    private static string? ValidateEndpoint(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "Укажите адрес (хост) WebSocket-сервера OBS, например localhost.";
        }

        if (port is < 1 or > 65535)
        {
            return "Порт WebSocket-сервера OBS должен быть числом от 1 до 65535 (по умолчанию 4455).";
        }

        return null;
    }

    internal static Uri BuildUri(string host, int port)
    {
        string trimmed = host.Trim();
        bool needsBrackets = trimmed.Contains(':') && !trimmed.StartsWith('[');
        return new Uri($"ws://{(needsBrackets ? $"[{trimmed}]" : trimmed)}:{port}");
    }

    private static string DescribeFailure(Exception ex, CancellationToken userToken) => ex switch
    {
        ObsProtocolException protocol => protocol.Message,
        OperationCanceledException when userToken.IsCancellationRequested => "Операция отменена.",
        OperationCanceledException =>
            "OBS не ответил вовремя. Проверьте, что по указанному адресу и порту работает именно WebSocket-сервер OBS.",
        _ => $"Не удалось подключиться к OBS ({ex.Message}). Убедитесь, что в OBS включён WebSocket-сервер " +
             "(Инструменты → Настройки WebSocket-сервера → флажок \"Включить сервер WebSocket\") и что " +
             "хост/порт/пароль совпадают с тем, что показывает там же кнопка \"Показать сведения о подключении\"."
    };

    private static string Describe<T>(ObsResponse<T> response) =>
        string.IsNullOrWhiteSpace(response.Comment) ? $"код {response.Code}" : $"код {response.Code}: {response.Comment}";

    /// <summary>Opens the socket and completes the Hello/Identify/Identified handshake, throwing on any failure.</summary>
    private static async Task<ClientWebSocket> ConnectAndIdentifyAsync(
        string host, int port, string password, CancellationToken cancellationToken)
    {
        ClientWebSocket ws = new();

        try
        {
            await ws.ConnectAsync(BuildUri(host, port), cancellationToken).ConfigureAwait(false);

            string? authString = null;
            using (JsonDocument hello = await ReceiveJsonAsync(ws, cancellationToken).ConfigureAwait(false))
            {
                JsonElement root = hello.RootElement;
                if (!TryGetInt(root, "op", out int helloOp) || helloOp != 0
                    || !root.TryGetProperty("d", out JsonElement helloData) || helloData.ValueKind != JsonValueKind.Object)
                {
                    throw new ObsProtocolException(
                        "Сервер по этому адресу ответил не как OBS WebSocket (нет приветствия Hello). Проверьте порт.");
                }

                if (helloData.TryGetProperty("authentication", out JsonElement authentication)
                    && authentication.ValueKind == JsonValueKind.Object)
                {
                    authString = ComputeAuthString(
                        password ?? "",
                        GetStringOrEmpty(authentication, "salt"),
                        GetStringOrEmpty(authentication, "challenge"));
                }
            }

            Dictionary<string, object?> identifyData = new()
            {
                ["rpcVersion"] = RpcVersion,
                ["eventSubscriptions"] = 0
            };
            if (authString is not null)
            {
                identifyData["authentication"] = authString;
            }

            await SendJsonAsync(ws, new Dictionary<string, object?> { ["op"] = 1, ["d"] = identifyData }, cancellationToken)
                .ConfigureAwait(false);

            using JsonDocument identified = await ReceiveJsonAsync(ws, cancellationToken).ConfigureAwait(false);
            if (!TryGetInt(identified.RootElement, "op", out int op) || op != 2)
            {
                throw new ObsProtocolException("OBS не подтвердил подключение (ожидался ответ Identified).");
            }

            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket? ws)
    {
        if (ws is null)
        {
            return;
        }

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort close - the connection is going away either way.
        }

        ws.Dispose();
    }

    internal static string ComputeAuthString(string password, string salt, string challenge)
    {
        byte[] secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        string base64Secret = Convert.ToBase64String(secretHash);
        byte[] authHash = SHA256.HashData(Encoding.UTF8.GetBytes(base64Secret + challenge));
        return Convert.ToBase64String(authHash);
    }

    private static Task<ObsResponse<bool>> SendRequestAsync(
        ClientWebSocket ws, string requestType, Dictionary<string, object?> requestData, CancellationToken cancellationToken) =>
        SendRequestAsync(ws, requestType, requestData, _ => true, cancellationToken);

    /// <summary>
    /// Sends one request and waits for *its* response: messages that are not a RequestResponse
    /// with the matching request id (events, stray replies) are skipped rather than mistaken
    /// for the answer.
    /// </summary>
    private static async Task<ObsResponse<T>> SendRequestAsync<T>(
        ClientWebSocket ws,
        string requestType,
        Dictionary<string, object?> requestData,
        Func<JsonElement, T> parseResponseData,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        Dictionary<string, object?> request = new()
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

        while (true)
        {
            using JsonDocument response = await ReceiveJsonAsync(ws, cancellationToken).ConfigureAwait(false);
            JsonElement root = response.RootElement;

            if (!TryGetInt(root, "op", out int op) || op != 7
                || !root.TryGetProperty("d", out JsonElement d) || d.ValueKind != JsonValueKind.Object
                || !d.TryGetProperty("requestId", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() != requestId)
            {
                continue;
            }

            if (!d.TryGetProperty("requestStatus", out JsonElement status) || status.ValueKind != JsonValueKind.Object)
            {
                throw new ObsProtocolException("OBS прислал ответ без статуса запроса.");
            }

            bool success = status.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.True;
            int code = TryGetInt(status, "code", out int parsedCode) ? parsedCode : -1;
            string comment = GetStringOrEmpty(status, "comment");

            T? data = default;
            if (success && d.TryGetProperty("responseData", out JsonElement responseData))
            {
                data = parseResponseData(responseData);
            }

            return new ObsResponse<T>(success, code, comment, data);
        }
    }

    private static IReadOnlyList<ObsFilterInfo> ParseFilterList(JsonElement responseData)
    {
        List<ObsFilterInfo> filters = new();

        if (responseData.ValueKind == JsonValueKind.Object
            && responseData.TryGetProperty("filters", out JsonElement array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                int index = TryGetInt(item, "filterIndex", out int parsedIndex) ? parsedIndex : 0;
                filters.Add(new ObsFilterInfo(GetStringOrEmpty(item, "filterName"), GetStringOrEmpty(item, "filterKind"), index));
            }
        }

        return filters;
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static string GetStringOrEmpty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16384];
        using MemoryStream ms = new();

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw DescribeClose(ws);
            }

            ms.Write(buffer, 0, result.Count);
            if (ms.Length > MaxMessageBytes)
            {
                throw new ObsProtocolException("OBS прислал слишком большое сообщение.");
            }
        }
        while (!result.EndOfMessage);

        ms.Position = 0;
        try
        {
            return await JsonDocument.ParseAsync(ms, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new ObsProtocolException("Сервер прислал не JSON — по этому адресу работает не OBS WebSocket. Проверьте порт.");
        }
    }

    /// <summary>Turns the close frame OBS sent into a message that names the real problem.</summary>
    private static ObsProtocolException DescribeClose(ClientWebSocket ws)
    {
        int code = ws.CloseStatus is { } status ? (int)status : 0;

        return code switch
        {
            CloseCodeAuthenticationFailed => new ObsProtocolException(
                "Неверный пароль WebSocket-сервера OBS (или пароль не указан). Пароль показывается в OBS: " +
                "Инструменты → Настройки WebSocket-сервера → Показать сведения о подключении."),
            CloseCodeUnsupportedRpcVersion => new ObsProtocolException(
                "Эта версия OBS использует версию протокола WebSocket, которую приложение не поддерживает."),
            _ => new ObsProtocolException(
                $"OBS закрыл соединение{(code != 0 ? $" (код {code}" + (string.IsNullOrWhiteSpace(ws.CloseStatusDescription) ? ")" : $": {ws.CloseStatusDescription})") : "")} до ответа.")
        };
    }

    private sealed record ObsResponse<T>(bool Success, int Code, string Comment, T? Data);

    private sealed record ObsFilterInfo(string Name, string Kind, int Index);

    /// <summary>A failure whose message is already written for the end user.</summary>
    private sealed class ObsProtocolException : Exception
    {
        public ObsProtocolException(string message)
            : base(message)
        {
        }
    }
}
