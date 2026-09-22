using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NeuroCamera.Engine;
using Xunit;

namespace NeuroCamera.Tests;

public class ObsWebSocketClientTests
{
    private const string Host = "127.0.0.1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly ObsEquivalentCalculator.ObsColorCorrectionValues SampleValues =
        new(Gamma: 0.25, Contrast: 0.1, Brightness: 0.05, Saturation: 0.0, HueShift: 0.0, Opacity: 1.0);

    // ---- connection / authentication -------------------------------------------------------

    [Fact]
    public async Task Connects_to_a_server_without_authentication()
    {
        using FakeObsServer server = new();

        (bool success, string message) = await ObsWebSocketClient.TestConnectionAsync(Host, server.Port, "", Timeout, default);

        Assert.True(success, message);
    }

    [Fact]
    public async Task Authenticates_with_the_value_documented_for_the_protocols_example_credentials()
    {
        using FakeObsServer server = new() { RequirePassword = true };

        (bool success, string message) = await ObsWebSocketClient.TestConnectionAsync(
            Host, server.Port, FakeObsServer.DocPassword, Timeout, default);

        Assert.True(success, message);
    }

    [Fact]
    public void Authentication_string_equals_the_independently_computed_value()
    {
        string actual = ObsWebSocketClient.ComputeAuthString(
            FakeObsServer.DocPassword, FakeObsServer.DocSalt, FakeObsServer.DocChallenge);

        Assert.Equal(FakeObsServer.DocExpectedAuthentication, actual);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_as_a_wrong_password()
    {
        using FakeObsServer server = new() { RequirePassword = true };

        (bool success, string message) = await ObsWebSocketClient.TestConnectionAsync(
            Host, server.Port, "definitely-wrong", Timeout, default);

        Assert.False(success);
        Assert.Contains("Неверный пароль", message);
        Assert.DoesNotContain("включён WebSocket-сервер", message);
    }

    [Fact]
    public async Task A_missing_password_is_reported_the_same_way()
    {
        using FakeObsServer server = new() { RequirePassword = true };

        (bool success, string message) = await ObsWebSocketClient.TestConnectionAsync(Host, server.Port, "", Timeout, default);

        Assert.False(success);
        Assert.Contains("Неверный пароль", message);
    }

    [Fact]
    public async Task A_server_that_never_answers_ends_with_a_timeout_message_instead_of_hanging()
    {
        using FakeObsServer server = new() { Silent = true };

        Stopwatch stopwatch = Stopwatch.StartNew();
        (bool success, string message) = await ObsWebSocketClient.TestConnectionAsync(
            Host, server.Port, "", TimeSpan.FromMilliseconds(600), default);
        stopwatch.Stop();

        Assert.False(success);
        Assert.Contains("не ответил", message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task A_service_that_is_not_obs_is_recognised_as_such()
    {
        using FakeObsServer server = new() { SendGarbageInsteadOfHello = true };

        (bool success, string message) = await ObsWebSocketClient.TestConnectionAsync(Host, server.Port, "", Timeout, default);

        Assert.False(success);
        Assert.Contains("не OBS", message);
    }

    [Fact]
    public async Task Nothing_listening_on_the_port_gives_a_connection_message_and_does_not_throw()
    {
        int closedPort = GetUnusedPort();

        (bool success, string message) = await ObsWebSocketClient.TestConnectionAsync(Host, closedPort, "", Timeout, default);

        Assert.False(success);
        Assert.Contains("Не удалось подключиться", message);
    }

    [Fact]
    public async Task An_out_of_range_port_is_rejected_before_connecting()
    {
        (bool zeroOk, string zeroMessage) = await ObsWebSocketClient.TestConnectionAsync(Host, 0, "", Timeout, default);
        (bool bigOk, string bigMessage) = await ObsWebSocketClient.TestConnectionAsync(Host, 70000, "", Timeout, default);

        Assert.False(zeroOk);
        Assert.False(bigOk);
        Assert.Contains("Порт", zeroMessage);
        Assert.Contains("Порт", bigMessage);
    }

    [Fact]
    public async Task An_empty_host_is_rejected_before_connecting()
    {
        (bool success, string message) = await ObsWebSocketClient.TestConnectionAsync("  ", 4455, "", Timeout, default);

        Assert.False(success);
        Assert.Contains("адрес", message);
    }

    [Fact]
    public void Ipv6_literals_are_wrapped_in_brackets()
    {
        Assert.Equal("ws://[::1]:4455/", ObsWebSocketClient.BuildUri("::1", 4455).ToString());
        Assert.Equal("ws://[::1]:4455/", ObsWebSocketClient.BuildUri("[::1]", 4455).ToString());
        Assert.Equal("ws://localhost:4455/", ObsWebSocketClient.BuildUri(" localhost ", 4455).ToString());
    }

    // ---- applying the filter ---------------------------------------------------------------

    [Fact]
    public async Task A_missing_filter_is_created_as_the_current_v2_kind_with_the_v2_opacity_scale()
    {
        using FakeObsServer server = new();
        server.Sources["Camera"] = new List<FakeObsServer.FakeFilter>();

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, server.Port, "", "Camera", "NeuroCamera", SampleValues, Timeout, default);

        Assert.True(success, message);
        Assert.Contains("создан", message);
        Assert.Equal(new[] { "GetSourceFilterList", "CreateSourceFilter" }, server.RequestTypes.ToArray());

        JsonElement create = server.Single("CreateSourceFilter").Data;
        Assert.Equal("color_filter_v2", create.GetProperty("filterKind").GetString());
        Assert.Equal("NeuroCamera", create.GetProperty("filterName").GetString());

        JsonElement settings = create.GetProperty("filterSettings");
        Assert.Equal(1.0, settings.GetProperty("opacity").GetDouble(), 6);
        Assert.Equal(0.25, settings.GetProperty("gamma").GetDouble(), 6);
        Assert.Equal(0.1, settings.GetProperty("contrast").GetDouble(), 6);
        Assert.Equal(0.05, settings.GetProperty("brightness").GetDouble(), 6);
        Assert.Equal(0.0, settings.GetProperty("saturation").GetDouble(), 6);
        Assert.Equal(0.0, settings.GetProperty("hue_shift").GetDouble(), 6);
    }

    [Fact]
    public async Task An_existing_v2_filter_is_updated_in_place_without_creating_or_removing_anything()
    {
        using FakeObsServer server = new();
        server.Sources["Camera"] = new List<FakeObsServer.FakeFilter>
        {
            new("Sharpen", "sharpness_filter_v2"),
            new("NeuroCamera", "color_filter_v2")
        };

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, server.Port, "", "Camera", "NeuroCamera", SampleValues, Timeout, default);

        Assert.True(success, message);
        Assert.Contains("применены", message);
        Assert.Equal(new[] { "GetSourceFilterList", "SetSourceFilterSettings" }, server.RequestTypes.ToArray());

        JsonElement update = server.Single("SetSourceFilterSettings").Data;
        Assert.Equal("NeuroCamera", update.GetProperty("filterName").GetString());
        Assert.False(update.GetProperty("overlay").GetBoolean());
        Assert.Equal(1.0, update.GetProperty("filterSettings").GetProperty("opacity").GetDouble(), 6);
    }

    [Fact]
    public async Task An_obsolete_v1_filter_is_replaced_by_a_v2_filter_at_the_same_position()
    {
        using FakeObsServer server = new();
        server.Sources["Camera"] = new List<FakeObsServer.FakeFilter>
        {
            new("First", "sharpness_filter_v2"),
            new("Second", "sharpness_filter_v2"),
            new("NeuroCamera", "color_filter"),
            new("Last", "sharpness_filter_v2")
        };

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, server.Port, "", "Camera", "NeuroCamera", SampleValues, Timeout, default);

        Assert.True(success, message);
        Assert.Contains("устаревшего типа", message);
        Assert.Equal(
            new[] { "GetSourceFilterList", "RemoveSourceFilter", "CreateSourceFilter", "SetSourceFilterIndex" },
            server.RequestTypes.ToArray());
        Assert.Equal("color_filter_v2", server.Single("CreateSourceFilter").Data.GetProperty("filterKind").GetString());
        Assert.Equal(2, server.Single("SetSourceFilterIndex").Data.GetProperty("filterIndex").GetInt32());

        FakeObsServer.FakeFilter[] finalState = server.Sources["Camera"].ToArray();
        Assert.Equal(new[] { "First", "Second", "NeuroCamera", "Last" }, finalState.Select(f => f.Name).ToArray());
        Assert.Equal("color_filter_v2", finalState[2].Kind);
    }

    [Fact]
    public async Task A_filter_of_an_unrelated_kind_with_the_same_name_is_never_modified()
    {
        using FakeObsServer server = new();
        server.Sources["Camera"] = new List<FakeObsServer.FakeFilter> { new("NeuroCamera", "clut_filter") };

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, server.Port, "", "Camera", "NeuroCamera", SampleValues, Timeout, default);

        Assert.False(success);
        Assert.Contains("другого типа", message);
        Assert.Contains("clut_filter", message);
        Assert.Equal(new[] { "GetSourceFilterList" }, server.RequestTypes.ToArray());
        Assert.Equal("clut_filter", server.Sources["Camera"][0].Kind);
    }

    [Fact]
    public async Task A_missing_source_is_reported_by_name()
    {
        using FakeObsServer server = new();

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, server.Port, "", "No such source", "NeuroCamera", SampleValues, Timeout, default);

        Assert.False(success);
        Assert.Contains("No such source", message);
        Assert.Contains("не найден", message);
        Assert.Equal(new[] { "GetSourceFilterList" }, server.RequestTypes.ToArray());
    }

    [Fact]
    public async Task Events_that_arrive_before_a_response_are_skipped()
    {
        using FakeObsServer server = new() { SendEventBeforeEachResponse = true };
        server.Sources["Camera"] = new List<FakeObsServer.FakeFilter>();

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, server.Port, "", "Camera", "NeuroCamera", SampleValues, Timeout, default);

        Assert.True(success, message);
        Assert.Equal(new[] { "GetSourceFilterList", "CreateSourceFilter" }, server.RequestTypes.ToArray());
    }

    [Fact]
    public async Task A_rejected_settings_update_is_reported_with_obs_status_code()
    {
        using FakeObsServer server = new();
        server.Sources["Camera"] = new List<FakeObsServer.FakeFilter> { new("NeuroCamera", "color_filter_v2") };
        server.ForcedFailures["SetSourceFilterSettings"] = (702, "Something went wrong.");

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, server.Port, "", "Camera", "NeuroCamera", SampleValues, Timeout, default);

        Assert.False(success);
        Assert.Contains("отклонил", message);
        Assert.Contains("702", message);
    }

    [Fact]
    public async Task A_failed_creation_is_reported_with_obs_status_code()
    {
        using FakeObsServer server = new();
        server.Sources["Camera"] = new List<FakeObsServer.FakeFilter>();
        server.ForcedFailures["CreateSourceFilter"] = (607, "Your specified filter kind is not supported by OBS.");

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, server.Port, "", "Camera", "NeuroCamera", SampleValues, Timeout, default);

        Assert.False(success);
        Assert.Contains("607", message);
    }

    [Fact]
    public async Task Blank_source_or_filter_names_are_rejected_without_connecting()
    {
        (bool noSource, string sourceMessage) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, 4455, "", "  ", "NeuroCamera", SampleValues, Timeout, default);
        (bool noFilter, string filterMessage) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, 4455, "", "Camera", "", SampleValues, Timeout, default);

        Assert.False(noSource);
        Assert.False(noFilter);
        Assert.Contains("имя источника", sourceMessage);
        Assert.Contains("имя фильтра", filterMessage);
    }

    [Fact]
    public async Task Applying_also_works_through_an_authenticated_connection()
    {
        using FakeObsServer server = new() { RequirePassword = true };
        server.Sources["Camera"] = new List<FakeObsServer.FakeFilter>();

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            Host, server.Port, FakeObsServer.DocPassword, "Camera", "NeuroCamera", SampleValues, Timeout, default);

        Assert.True(success, message);
    }

    [Fact]
    public async Task A_cancelled_operation_reports_cancellation_not_a_timeout()
    {
        using FakeObsServer server = new() { Silent = true };
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));

        (bool success, string message) = await ObsWebSocketClient.TestConnectionAsync(Host, server.Port, "", Timeout, cts.Token);

        Assert.False(success);
        Assert.Contains("отменена", message);
    }

    private static int GetUnusedPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
