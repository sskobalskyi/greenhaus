using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Greenhaus.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Greenhaus.Api.Tests;

/// <summary>What the service sends to Docs-Brain, pinned with a stub: path, headers, body, token and how failures surface.</summary>
public class HttpAssistantClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return respond(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Stream(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static (HttpAssistantClient Client, Stub Chat, Stub Token) Make(DocsBrainOptions options, Func<HttpRequestMessage, string?, HttpResponseMessage> chat,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? token = null)
    {
        var chatStub = new Stub(chat);
        var tokenStub = new Stub(token ?? ((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"service-jwt","expires_in":300}""", Encoding.UTF8, "application/json") }));
        var tokens = new DocsBrainTokens(Options.Create(options), new Factory(tokenStub), TimeProvider.System);
        return (new HttpAssistantClient(new HttpClient(chatStub) { BaseAddress = new Uri("http://docs-brain/") }, tokens), chatStub, tokenStub);
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> events)
    {
        var list = new List<string>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    private const string Answer = "data: {\"type\":\"token\",\"text\":\"Hi\"}\n\ndata: {\"type\":\"done\",\"mode\":\"answer\",\"tokens\":1}\n\n";

    [Fact]
    public async Task A_question_goes_to_api_chat_with_the_service_token_the_tenant_and_the_context()
    {
        var (client, chat, token) = Make(new DocsBrainOptions { TokenUrl = "http://keycloak/token", ClientId = "greenhaus-assistant", ClientSecret = "s" }, (_, _) => Stream(Answer));

        var events = await Collect(client.AskAsync("11111111-2222-3333-4444-555555555555", "Is my basil ok?", "Plant 1: Basil.", default));

        var (request, body) = chat.Seen.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/chat", request.RequestUri!.AbsolutePath);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "service-jwt"), request.Headers.Authorization);
        Assert.Equal("11111111-2222-3333-4444-555555555555", request.Headers.GetValues("X-Tenant-Id").Single());
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "text/event-stream");
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("Is my basil ok?", json["question"]!.GetValue<string>());
        Assert.Equal("Plant 1: Basil.", json["context"]!.GetValue<string>());

        // The token was asked for with client credentials.
        Assert.Contains("grant_type=client_credentials", token.Seen.Single().Body);
        Assert.Contains("client_id=greenhaus-assistant", token.Seen.Single().Body);

        Assert.Equal(["{\"type\":\"token\",\"text\":\"Hi\"}", "{\"type\":\"done\",\"mode\":\"answer\",\"tokens\":1}"], events);
    }

    [Fact]
    public async Task A_static_token_is_used_without_asking_keycloak()
    {
        var (client, chat, token) = Make(new DocsBrainOptions { StaticToken = "fixed" }, (_, _) => Stream(Answer));
        await Collect(client.AskAsync("t", "q", "c", default));

        Assert.Equal("fixed", chat.Seen.Single().Request.Headers.Authorization!.Parameter);
        Assert.Empty(token.Seen);
    }

    [Fact]
    public async Task An_assistant_that_is_not_configured_is_never_called()
    {
        var (client, chat, _) = Make(new DocsBrainOptions(), (_, _) => Stream(Answer));
        await Assert.ThrowsAsync<AssistantUnavailableException>(() => Collect(client.AskAsync("t", "q", "c", default)));
        Assert.Empty(chat.Seen);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_refusal_or_a_server_error_is_unavailable(HttpStatusCode status)
    {
        var (client, _, _) = Make(new DocsBrainOptions { StaticToken = "x" }, (_, _) => new HttpResponseMessage(status));
        await Assert.ThrowsAsync<AssistantUnavailableException>(() => Collect(client.AskAsync("t", "q", "c", default)));
    }

    [Fact]
    public async Task An_unreachable_assistant_and_a_failing_token_service_are_unavailable()
    {
        var (down, _, _) = Make(new DocsBrainOptions { StaticToken = "x" }, (_, _) => throw new HttpRequestException("refused"));
        await Assert.ThrowsAsync<AssistantUnavailableException>(() => Collect(down.AskAsync("t", "q", "c", default)));

        var (noToken, chat, _) = Make(new DocsBrainOptions { TokenUrl = "http://keycloak/token" }, (_, _) => Stream(Answer), (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<AssistantUnavailableException>(() => Collect(noToken.AskAsync("t", "q", "c", default)));
        Assert.Empty(chat.Seen);
    }

    [Fact]
    public async Task Lines_that_are_not_data_are_skipped()
    {
        var (client, _, _) = Make(new DocsBrainOptions { StaticToken = "x" }, (_, _) => Stream(": keep-alive\n\nevent: message\ndata: {\"type\":\"done\",\"mode\":\"no_results\"}\n\n"));
        Assert.Equal(["{\"type\":\"done\",\"mode\":\"no_results\"}"], await Collect(client.AskAsync("t", "q", "c", default)));
    }
}