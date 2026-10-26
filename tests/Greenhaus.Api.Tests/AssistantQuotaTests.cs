using System.Net;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Api.Tests;

/// <summary>
/// The monthly quota holds when questions start together: a place is taken before a question is sent and given back, or turned into a
/// counted answer, when it ends.
/// </summary>
[Collection("api")]
public class AssistantQuotaTests(GreenhausFixture api)
{
    private Task<HttpResponseMessage> Ask(string user, string potId, string question) =>
        api.Client.Call("POST", "/pots/{potId}/assistant/ask", new { potId }, new { question }, null, GreenhausFixture.As(user));

    private async Task<string> NewPot(string user)
    {
        var r = await api.Client.Call("POST", "/pots", null, new { name = "Herbs", kind = "pot" }, null, GreenhausFixture.As(user));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await api.Client.Node(r))["id"]!.GetValue<string>();
    }

    private static string Unique(string prefix = "") => prefix + "Is it ok? " + Guid.NewGuid().ToString("N");

    private static DateOnly ThisMonth => AssistantUsage.MonthOf(DateTimeOffset.UtcNow);

    private Task<AssistantUsageEntity?> Row(string user) =>
        api.WithDb(db => db.AssistantUsage.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == user && u.Month == ThisMonth));

    private Task Seed(string user, int count, int reserved, DateTimeOffset? reservedAt) => api.WithDb(async db =>
    {
        db.AssistantUsage.Add(new AssistantUsageEntity { UserId = user, Month = ThisMonth, Count = count, Reserved = reserved, ReservedAt = reservedAt });
        await db.SaveChangesAsync();
        return 0;
    });

    private static async Task<bool> Until(Func<Task<bool>> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Fact]
    public async Task Questions_that_start_together_cannot_overshoot_the_quota()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        // The Novice tier allows ten: two are left, and five questions start at the same moment.
        await Seed(user, count: 8, reserved: 0, reservedAt: null);
        var before = api.Docs.SlowStarted;

        var asks = Enumerable.Range(0, 5).Select(_ => Task.Run(() => Ask(user, pot, Unique("SLOW:")))).ToList();

        // Only two get a place. The other three are refused at once, while the two are still waiting for the assistant.
        Assert.True(await Until(() => Task.FromResult(asks.Count(a => a.IsCompleted) == 3)), "three questions should have been refused at once");
        await Task.Delay(500);
        Assert.Equal(2, api.Docs.SlowStarted - before);
        Assert.All(asks.Where(a => a.IsCompleted), a => Assert.Equal(HttpStatusCode.Forbidden, a.Result.StatusCode));
        var held = await Row(user);
        Assert.Equal(2, held!.Reserved);
        Assert.Equal(8, held.Count);

        api.Docs.Gate.Release(2);
        var results = await Task.WhenAll(asks);
        Assert.Equal(2, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Forbidden));

        var done = await Row(user);
        Assert.Equal(10, done!.Count);
        Assert.Equal(0, done.Reserved);
    }

    [Theory]
    [InlineData("RETRIEVAL:")]
    [InlineData("NORESULTS:")]
    [InlineData("CUTOFF:")]
    [InlineData("NODONE:")]
    [InlineData("DOWN:")]
    public async Task A_question_that_ends_without_an_answer_gives_its_place_back(string prefix)
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);

        var response = await Ask(user, pot, Unique(prefix));
        await response.Content.ReadAsStringAsync();

        var row = await Row(user);
        Assert.NotNull(row);
        Assert.Equal(0, row.Count);
        Assert.Equal(0, row.Reserved);
    }

    [Fact]
    public async Task An_answered_question_turns_its_place_into_a_counted_one()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        Assert.Equal(HttpStatusCode.OK, (await Ask(user, pot, Unique())).StatusCode);

        var row = await Row(user);
        Assert.Equal(1, row!.Count);
        Assert.Equal(0, row.Reserved);
    }

    [Fact]
    public async Task A_person_who_goes_away_in_the_middle_of_a_question_gives_the_place_back_without_being_charged()
    {
        var user = GreenhausFixture.NewUser();
        var pot = await NewPot(user);
        var before = api.Docs.SlowStarted;

        using var cancel = new CancellationTokenSource();
        var request = new HttpRequestMessage(HttpMethod.Post, api.Client.Contract.Url($"/pots/{pot}/assistant/ask"))
        {
            Content = new StringContent($$"""{"question":"{{Unique("SLOW:")}}"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-User-Id", user);
        var pending = api.Client.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);

        Assert.True(await Until(() => Task.FromResult(api.Docs.SlowStarted > before)), "the question should have reached the assistant");
        Assert.Equal(1, (await Row(user))!.Reserved);

        cancel.Cancel();
        // How the client sees the end (an exception, or a response the server managed to write) does not matter here: the place does.
        try { (await pending).Dispose(); } catch (OperationCanceledException) { }
        Assert.True(await Until(async () => (await Row(user))!.Reserved == 0), "the place should be given back");
        Assert.Equal(0, (await Row(user))!.Count);
    }

    [Fact]
    public async Task Places_held_by_running_questions_count_but_places_of_a_process_that_died_do_not_last()
    {
        var fresh = GreenhausFixture.NewUser();
        var pot = await NewPot(fresh);
        // Ten questions are running right now, so an eleventh has to wait for one of them.
        await Seed(fresh, count: 0, reserved: 10, reservedAt: DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Forbidden, (await Ask(fresh, pot, Unique())).StatusCode);

        // Ten places that were taken an hour ago belong to a process that died: they are dropped.
        var stale = GreenhausFixture.NewUser();
        pot = await NewPot(stale);
        await Seed(stale, count: 0, reserved: 10, reservedAt: DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(HttpStatusCode.OK, (await Ask(stale, pot, Unique())).StatusCode);
        var row = await Row(stale);
        Assert.Equal(1, row!.Count);
        Assert.Equal(0, row.Reserved);
    }
}
