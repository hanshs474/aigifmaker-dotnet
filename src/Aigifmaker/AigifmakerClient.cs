using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aigifmaker
{
    /// <summary>Why a call failed, split by what to do next.</summary>
    public enum AigifmakerErrorKind
    {
        /// <summary>No API key, or the key is invalid or deleted. Create one at https://aigifmaker.ai/settings/apikeys.</summary>
        Auth,
        /// <summary>Not enough credits left for this GIF. Top up at https://aigifmaker.ai/pricing.</summary>
        Quota,
        /// <summary>The content filter refused the prompt, or the account is cooling down after a refusal. Reword rather than retry.</summary>
        Rejected,
        /// <summary>The GIF was accepted and then failed; the message carries the service's reason. Failed runs are refunded.</summary>
        Failed,
        /// <summary>The wait ran out; the GIF may still finish in your AI GIF Maker history.</summary>
        Timeout,
        /// <summary>Anything else the service answered, or a transport failure.</summary>
        Other,
    }

    /// <summary>Every error this library throws on purpose. Catch the subclasses, or branch on <see cref="Kind"/>.</summary>
    public class AigifmakerException : Exception
    {
        /// <summary>What went wrong.</summary>
        public AigifmakerErrorKind Kind { get; }
        /// <summary>The JSON <c>code</c> of the answer (-1 for most refusals), or null when there was no JSON answer.</summary>
        public int? Code { get; }
        /// <summary>The HTTP status, or 0 when there was no response. The service answers 200 even for refusals.</summary>
        public int HttpStatus { get; }

        /// <summary>Creates an exception of a given kind.</summary>
        public AigifmakerException(AigifmakerErrorKind kind, string message, int? code = null, int httpStatus = 0)
            : base(message)
        {
            Kind = kind;
            Code = code;
            HttpStatus = httpStatus;
        }

        /// <summary>Map a refusal (<c>code != 0</c>) to the error that says what to do next.</summary>
        public static AigifmakerException FromAnswer(int code, string? message, int httpStatus = 200)
        {
            var msg = string.IsNullOrEmpty(message) ? "request refused (code " + code + ")" : message!;
            var low = msg.ToLowerInvariant();
            if (low.Contains("invalid api key") || low.Contains("no auth"))
                return new AuthException(msg, code, httpStatus);
            if (low.Contains("insufficient credits"))
                return new QuotaException(msg + (low.Contains("/pricing") ? "" : "; top up at " + AigifmakerClient.PricingUrl), code, httpStatus);
            if (code != -1)
                return new RejectedException(msg, code, httpStatus);
            return new AigifmakerException(AigifmakerErrorKind.Other, msg, code, httpStatus);
        }
    }

    /// <summary>No API key, or the key is invalid or deleted.</summary>
    public sealed class AuthException : AigifmakerException
    {
        /// <summary>Creates an auth error.</summary>
        public AuthException(string message, int? code = null, int httpStatus = 0) : base(AigifmakerErrorKind.Auth, message, code, httpStatus) { }
    }

    /// <summary>Not enough credits left for this GIF.</summary>
    public sealed class QuotaException : AigifmakerException
    {
        /// <summary>Creates a quota error.</summary>
        public QuotaException(string message, int? code = null, int httpStatus = 0) : base(AigifmakerErrorKind.Quota, message, code, httpStatus) { }
    }

    /// <summary>The prompt was refused by the content filter, or the account is cooling down after a refusal.</summary>
    public sealed class RejectedException : AigifmakerException
    {
        /// <summary>Creates a rejection error.</summary>
        public RejectedException(string message, int? code = null, int httpStatus = 0) : base(AigifmakerErrorKind.Rejected, message, code, httpStatus) { }
    }

    /// <summary>Where a submitted GIF stands.</summary>
    public sealed class AigifmakerStatus
    {
        /// <summary>The task id.</summary>
        public string Id { get; }
        /// <summary><c>pending</c>, <c>processing</c>, <c>success</c>, <c>failed</c> or <c>canceled</c>.</summary>
        public string Status { get; }
        /// <summary>The finished GIF URLs once <c>success</c>.</summary>
        public IReadOnlyList<string> Images { get; }
        /// <summary>The first GIF URL, or null until the GIF is ready.</summary>
        public string? Url => Images.Count > 0 ? Images[0] : null;
        /// <summary>True when the GIF carries the aigifmaker.ai badge (the free GIF).</summary>
        public bool Watermarked { get; }
        /// <summary>True while the GIF waits in the queue for accounts that have not bought credits; keep polling.</summary>
        public bool Queued { get; }
        /// <summary>Seconds the queue expects to wait, when queued.</summary>
        public int? QueueEta { get; }
        /// <summary>Why the GIF failed, when it did.</summary>
        public string? ErrorMessage { get; }
        /// <summary>The failure class, e.g. <c>moderation</c>.</summary>
        public string? FailureKind { get; }
        /// <summary>What happened to the credit of a failed GIF.</summary>
        public string? RefundNote { get; }
        /// <summary>The whole <c>data</c> object as the service sent it.</summary>
        public JsonElement Raw { get; }
        /// <summary>True once the GIF is ready.</summary>
        public bool IsSuccess => Status == "success";
        /// <summary>True when the GIF failed or was canceled.</summary>
        public bool IsFailed => Status == "failed" || Status == "canceled";

        internal AigifmakerStatus(JsonElement data)
        {
            Raw = data.Clone();
            Id = Str(data, "id") ?? "";
            Status = Str(data, "status") ?? "";
            var images = new List<string>();
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var v in arr.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String) images.Add(v.GetString()!);
            Images = images;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("watermarked", out var w))
            {
                if (w.ValueKind == JsonValueKind.True) Watermarked = true;
                else if (w.ValueKind == JsonValueKind.Array)
                    foreach (var v in w.EnumerateArray()) if (v.ValueKind == JsonValueKind.True) Watermarked = true;
            }
            Queued = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("queued", out var q) && q.ValueKind == JsonValueKind.True;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("queueEta", out var eta) && eta.ValueKind == JsonValueKind.Number)
                QueueEta = (int)eta.GetDouble();
            ErrorMessage = Str(data, "errorMessage");
            FailureKind = Str(data, "failureKind");
            RefundNote = Str(data, "refundNote");
        }

        internal static string? Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
                : null;
    }

    /// <summary>
    /// Official SDK for AI GIF Maker (https://aigifmaker.ai): turn text into animated GIFs.
    /// Create a key at https://aigifmaker.ai/settings/apikeys. Each GIF costs 1 credit on your account, the same as on the website.
    /// </summary>
    public sealed class AigifmakerClient
    {
        /// <summary>Library version, sent in the User-Agent.</summary>
        public const string Version = "0.1.0";
        /// <summary>The service this library talks to.</summary>
        public const string DefaultBaseUrl = "https://aigifmaker.ai";
        /// <summary>Where an API key is created.</summary>
        public const string KeysUrl = DefaultBaseUrl + "/settings/apikeys";
        /// <summary>Where credits are bought.</summary>
        public const string PricingUrl = DefaultBaseUrl + "/pricing";

        static readonly HttpClient SharedHttp = new HttpClient { Timeout = System.TimeSpan.FromSeconds(60) };

        readonly string _apiKey;
        readonly HttpClient _http;
        readonly string _base;

        /// <summary>How long <see cref="GenerateAsync"/> waits for a GIF. Defaults to 5 minutes.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);
        /// <summary>How often the status is checked while waiting. Defaults to 3 seconds.</summary>
        public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>A client for one API key; null reads the AIGIFMAKER_API_KEY environment variable.</summary>
        public AigifmakerClient(string? apiKey = null, string? baseUrl = null, TimeSpan? timeout = null, HttpClient? http = null)
        {
            _apiKey = (apiKey ?? Environment.GetEnvironmentVariable("AIGIFMAKER_API_KEY") ?? "").Trim();
            _base = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
            _http = http ?? SharedHttp;
            if (timeout.HasValue) Timeout = timeout.Value;
        }

        /// <summary>Turn a prompt into an animated GIF and return its URL: submits, waits, and returns when the GIF is ready.</summary>
        public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
            => await WaitAsync(await SubmitAsync(prompt, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        /// <summary>Start a GIF without waiting and return its id; follow it with <see cref="StatusAsync"/>.</summary>
        public async Task<string> SubmitAsync(string prompt, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("prompt is required", nameof(prompt));
            var body = new Dictionary<string, object>
            {
                ["provider"] = "openlux",
                ["mediaType"] = "image",
                ["model"] = "gif-maker-v1",
                ["scene"] = "text-to-image",
                ["prompt"] = prompt,
                ["options"] = new Dictionary<string, object> { ["output_format"] = "gif" },
            };
            var data = await CallAsync("/api/ai/generate", body, ct).ConfigureAwait(false);
            var id = AigifmakerStatus.Str(data, "id");
            if (string.IsNullOrEmpty(id)) throw new AigifmakerException(AigifmakerErrorKind.Other, "the service returned no task id", 0, 200);
            return id!;
        }

        /// <summary>Ask where a GIF stands.</summary>
        public async Task<AigifmakerStatus> StatusAsync(string id, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id is required", nameof(id));
            var data = await CallAsync("/api/ai/query", new Dictionary<string, object> { ["taskId"] = id }, ct).ConfigureAwait(false);
            return new AigifmakerStatus(data);
        }

        /// <summary>The credits left on the account. Free to call.</summary>
        public async Task<int> CreditsAsync(CancellationToken ct = default)
        {
            var data = await CallAsync("/api/user/get-user-credits", new Dictionary<string, object>(), ct).ConfigureAwait(false);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("remainingCredits", out var left) && left.ValueKind == JsonValueKind.Number)
                return (int)left.GetDouble();
            throw new AigifmakerException(AigifmakerErrorKind.Other, "the service returned no credit balance", 0, 200);
        }

        async Task<string> WaitAsync(string id, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                AigifmakerStatus? s = null;
                try
                {
                    s = await StatusAsync(id, ct).ConfigureAwait(false);
                }
                catch (AigifmakerException e) when (e.Kind == AigifmakerErrorKind.Other && e.HttpStatus != 200)
                {
                    // a dropped poll (network, 5xx) is not a failed GIF
                }
                if (s != null)
                {
                    if (s.IsSuccess && s.Url != null) return s.Url;
                    if (s.IsFailed)
                    {
                        var why = s.ErrorMessage ?? s.Status;
                        if (s.FailureKind == "moderation") throw new RejectedException($"GIF {id} was refused: {why}", null, 200);
                        throw new AigifmakerException(AigifmakerErrorKind.Failed, $"GIF {id} failed: {why}", null, 200);
                    }
                }
                if (DateTime.UtcNow + PollEvery >= deadline) break;
                await Task.Delay(PollEvery, ct).ConfigureAwait(false);
            }
            throw new AigifmakerException(AigifmakerErrorKind.Timeout, $"deadline passed; GIF {id} may still finish in your AI GIF Maker history");
        }

        async Task<JsonElement> CallAsync(string path, object body, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _base + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            // Without a key the request still goes out: the service answers with a wall, which becomes an AuthException.
            if (_apiKey.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            req.Headers.TryAddWithoutValidation("User-Agent", "aigifmaker-dotnet/" + Version);
            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw new AigifmakerException(AigifmakerErrorKind.Other, e.Message);
            }
            string text;
            int status;
            using (res)
            {
                status = (int)res.StatusCode;
                text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            JsonElement json = default;
            try
            {
                if (!string.IsNullOrWhiteSpace(text)) json = JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException) { }
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.Number)
            {
                var msg = json.ValueKind == JsonValueKind.Object ? AigifmakerStatus.Str(json, "message") : null;
                throw new AigifmakerException(AigifmakerErrorKind.Other, msg ?? "unexpected answer (HTTP " + status + ")", null, status);
            }
            var code = (int)codeEl.GetDouble();
            if (code != 0) throw AigifmakerException.FromAnswer(code, AigifmakerStatus.Str(json, "message"), status);
            json.TryGetProperty("data", out var data);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("wall", out var wall) && wall.ValueKind == JsonValueKind.True)
                throw new AuthException("an API key is required; create one at " + KeysUrl, 0, status);
            return data;
        }
    }
}
