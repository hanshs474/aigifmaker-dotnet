// dotnet run --project tests/Aigifmaker.Tests: no test framework needed. Response bodies are the service's real shapes.
using System.Net;
using System.Text;
using System.Text.Json;
using Aigifmaker;

var failures = 0;
void Check(bool ok, string what) { if (!ok) { failures++; Console.WriteLine("FAIL " + what); } }

async Task<Exception?> Catch(Func<Task> f) { try { await f(); return null; } catch (Exception e) { return e; } }

var seen = new List<(string Path, string? Auth, JsonElement Body)>();
AigifmakerClient Client(Func<string, string> answer, string key = "sk-test")
    => new(key, "https://t", null, new HttpClient(new Handler(seen, answer))) { PollEvery = TimeSpan.Zero };

const string Submitted = "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"task_1\",\"status\":\"pending\",\"taskId\":\"up_9\",\"provider\":\"openlux\"}}";
const string Gif = "https://cdn.aigifmaker.ai/g/task_1.gif";

// success: pending -> processing -> success
{
    seen.Clear();
    var polls = new Queue<string>(new[]
    {
        "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"task_1\",\"status\":\"processing\",\"images\":[]}}",
        "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"task_1\",\"status\":\"success\",\"images\":[\"" + Gif + "\"],\"watermarked\":[false]}}",
    });
    var c = Client(p => p == "/api/ai/generate" ? Submitted : polls.Dequeue());
    Check(await c.GenerateAsync("a cat waving hello") == Gif, "success url");
    var b = seen[0].Body;
    Check(seen[0].Path == "/api/ai/generate" && seen[0].Auth == "Bearer sk-test", "submit path + bearer");
    Check(b.GetProperty("provider").GetString() == "openlux" && b.GetProperty("mediaType").GetString() == "image"
          && b.GetProperty("model").GetString() == "gif-maker-v1" && b.GetProperty("scene").GetString() == "text-to-image"
          && b.GetProperty("prompt").GetString() == "a cat waving hello"
          && b.GetProperty("options").GetProperty("output_format").GetString() == "gif", "fixed request body");
    Check(b.EnumerateObject().Count() == 6, "no extra fields in body");
    Check(seen[1].Path == "/api/ai/query" && seen[1].Body.GetProperty("taskId").GetString() == "task_1", "query uses data.id");
    Check(seen.Count == 3, "polled until success");
}

// queued: processing + queued:true keeps polling
{
    seen.Clear();
    var polls = new Queue<string>(new[]
    {
        "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"task_1\",\"status\":\"processing\",\"images\":[],\"queued\":true,\"queueEta\":40}}",
        "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"task_1\",\"status\":\"processing\",\"images\":[],\"queued\":true,\"queueEta\":5}}",
        "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"task_1\",\"status\":\"success\",\"images\":[\"" + Gif + "\"],\"watermarked\":[true]}}",
    });
    var c = Client(p => p == "/api/ai/generate" ? Submitted : polls.Dequeue());
    var id = await c.SubmitAsync("x");
    Check(id == "task_1", "submit id");
    var s = await c.StatusAsync(id);
    Check(s.Queued && s.QueueEta == 40 && s.Status == "processing" && s.Url == null, "queued status object");
    Check(await c.GenerateAsync("x") == Gif, "queued then success");
    var st = await Client(_ => "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"t\",\"status\":\"success\",\"images\":[\"" + Gif + "\"],\"watermarked\":[true]}}").StatusAsync("t");
    Check(st.IsSuccess && st.Watermarked && st.Url == Gif, "watermarked flag");
}

// failed run carries errorMessage
{
    var c = Client(p => p == "/api/ai/generate" ? Submitted
        : "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"task_1\",\"status\":\"failed\",\"images\":[],\"errorMessage\":\"upstream returned a still image\",\"refundNote\":\"credit refunded\"}}");
    var e = await Catch(() => c.GenerateAsync("x"));
    Check(e is AigifmakerException x && x.Kind == AigifmakerErrorKind.Failed && x.Message.Contains("upstream returned a still image"), "failed -> Failed with errorMessage");
}

// failed with failureKind moderation -> Rejected
{
    var c = Client(p => p == "/api/ai/generate" ? Submitted
        : "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"task_1\",\"status\":\"failed\",\"images\":[],\"errorMessage\":\"blocked\",\"failureKind\":\"moderation\"}}");
    Check(await Catch(() => c.GenerateAsync("x")) is RejectedException, "moderation -> Rejected");
}

// invalid key (real message)
{
    var c = Client(_ => "{\"code\":-1,\"message\":\"invalid API key — create one at https://aigifmaker.ai/settings/apikeys\"}", "sk-bad");
    Check(await Catch(() => c.CreditsAsync()) is AuthException, "credits invalid key -> Auth");
    Check(await Catch(() => c.SubmitAsync("x")) is AuthException, "submit invalid key -> Auth");
    Check(await Catch(() => Client(_ => "{\"code\":-1,\"message\":\"no auth\"}").StatusAsync("t")) is AuthException, "no auth -> Auth");
}

// wall (no key): request goes out without Authorization, answer becomes Auth
{
    seen.Clear();
    var c = Client(_ => "{\"code\":0,\"message\":\"ok\",\"data\":{\"wall\":true,\"reason\":\"anon_credits\",\"cost\":1,\"remaining\":0,\"grant\":0}}", "");
    var e = await Catch(() => c.SubmitAsync("x"));
    Check(e is AuthException && e.Message.Contains("/settings/apikeys"), "wall -> Auth");
    Check(seen.Count == 1 && seen[0].Auth == null, "no Authorization header without a key");
}

// insufficient credits
{
    var c = Client(_ => "{\"code\":-1,\"message\":\"insufficient credits\"}");
    var e = await Catch(() => c.SubmitAsync("x"));
    Check(e is QuotaException && e.Message.Contains("https://aigifmaker.ai/pricing"), "insufficient credits -> Quota");
}

// content cooldown (code neither 0 nor -1) -> Rejected; other -1 -> base error
{
    Check(await Catch(() => Client(_ => "{\"code\":429,\"message\":\"please wait before trying again\"}").SubmitAsync("x")) is RejectedException, "cooldown -> Rejected");
    var e = await Catch(() => Client(_ => "{\"code\":-1,\"message\":\"prompt too long\"}").SubmitAsync("x"));
    Check(e != null && e.GetType() == typeof(AigifmakerException) && ((AigifmakerException)e).Kind == AigifmakerErrorKind.Other && e.Message == "prompt too long", "other -1 -> base");
}

// credits
{
    var c = Client(_ => "{\"code\":0,\"message\":\"ok\",\"data\":{\"remainingCredits\":42,\"made\":3}}");
    Check(await c.CreditsAsync() == 42, "credits");
    Check(seen[seen.Count - 1].Path == "/api/user/get-user-credits", "credits path");
}

// timeout
{
    var c = Client(p => p == "/api/ai/generate" ? Submitted : "{\"code\":0,\"message\":\"ok\",\"data\":{\"id\":\"task_1\",\"status\":\"processing\"}}");
    c.Timeout = TimeSpan.FromMilliseconds(50);
    c.PollEvery = TimeSpan.FromMilliseconds(10);
    var e = await Catch(() => c.GenerateAsync("x"));
    Check(e is AigifmakerException x && x.Kind == AigifmakerErrorKind.Timeout, "timeout");
}

Check(await Catch(() => Client(_ => Submitted).SubmitAsync(" ")) is ArgumentException, "empty prompt");

if (failures > 0) { Console.WriteLine($"{failures} test(s) failed"); return 1; }
Console.WriteLine("all tests passed");
return 0;

sealed class Handler(List<(string, string?, JsonElement)> seen, Func<string, string> answer) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var auth = r.Headers.TryGetValues("Authorization", out var v) ? v.First() : null;
        seen.Add((r.RequestUri!.AbsolutePath, auth, JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)).RootElement.Clone()));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(r.RequestUri!.AbsolutePath), Encoding.UTF8, "application/json") };
    }
}
