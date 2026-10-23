using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Greenhaus.Api.Infrastructure;

namespace Greenhaus.Api.Tests;

/// <summary>
/// A Docs-Brain that streams a few events. What it does depends on a prefix of the question, so tests that share it do not
/// disturb each other: RETRIEVAL:, NORESULTS:, DOWN: (unreachable), CUTOFF: (breaks off mid-answer) and NODONE: (ends without done).
/// </summary>
public sealed class FakeDocsBrain : IAssistantClient
{
    public sealed record Call(string Tenant, string Question, string Context);

    public ConcurrentQueue<Call> Calls { get; } = new();

    public Call? CallFor(string question) => Calls.FirstOrDefault(c => c.Question == question);

    public async IAsyncEnumerable<string> AskAsync(string tenantId, string question, string context, [EnumeratorCancellation] CancellationToken ct)
    {
        Calls.Enqueue(new Call(tenantId, question, context));
        await Task.Yield();

        if (question.StartsWith("DOWN:", StringComparison.Ordinal)) throw new AssistantUnavailableException("down");
        if (question.StartsWith("NORESULTS:", StringComparison.Ordinal))
        {
            yield return """{"type":"done","mode":"no_results"}""";
            yield break;
        }
        if (question.StartsWith("RETRIEVAL:", StringComparison.Ordinal))
        {
            yield return """{"type":"sources","sources":[{"number":1,"title":"Basil care","text":"Water when the top soil is dry."}]}""";
            yield return """{"type":"done","mode":"retrieval_only","reason":"budget_exceeded"}""";
            yield break;
        }

        yield return """{"type":"token","text":"Looks "}""";
        if (question.StartsWith("CUTOFF:", StringComparison.Ordinal)) throw new AssistantUnavailableException("broke off");
        yield return """{"type":"token","text":"fine."}""";
        if (question.StartsWith("NODONE:", StringComparison.Ordinal)) yield break;
        yield return """{"type":"done","mode":"answer","tokens":2}""";
    }
}