# AI GIF Maker for .NET

Turn text into animated GIFs with AI GIF Maker. Native GIF generation — no video conversion. Type what you want and download your GIF in about a minute.

This is the official .NET SDK for [AI GIF Maker](https://aigifmaker.ai/?utm_source=nuget&utm_medium=package): one call takes a prompt and hands back the URL of a finished, looping GIF, ready for chat, docs, release notes or a bot reply.

```csharp
using Aigifmaker;

var gifs = new AigifmakerClient(); // reads AIGIFMAKER_API_KEY
string url = await gifs.GenerateAsync("a corgi in sunglasses dancing on a beach, confetti falling");
Console.WriteLine(url); // URL of the finished GIF
```

`GenerateAsync` submits the prompt, waits for the GIF and returns its URL.

## Install

```bash
dotnet add package Aigifmaker
```

Targets .NET 8 and .NET Standard 2.0. No dependencies beyond `System.Text.Json`.

## API key

Sign in on AI GIF Maker, create a key at https://aigifmaker.ai/settings/apikeys, then pass it to the constructor or export it:

```bash
export AIGIFMAKER_API_KEY=sk-...
```

A key runs on your account exactly like the website does, and every GIF shows up in your history there.

## Credits

Each GIF costs 1 credit on your AI GIF Maker account, the same as on the website. New accounts get one free GIF (it carries a small aigifmaker.ai badge). If a GIF fails, its credit is refunded. Credit packs are on the [pricing](https://aigifmaker.ai/pricing?utm_source=nuget&utm_medium=package) page.

## Submit now, collect later

```csharp
string id = await gifs.SubmitAsync("a paper plane looping over a city skyline at sunset");
AigifmakerStatus s = await gifs.StatusAsync(id); // s.Status: pending | processing | success | failed
if (s.IsSuccess) Console.WriteLine(s.Url);
int left = await gifs.CreditsAsync();            // free to call
```

Accounts that have not bought credits yet may wait in a short queue: `StatusAsync` then reports `processing` with `Queued == true` and `QueueEta` in seconds. That is normal; keep polling.

`GenerateAsync` polls every 3 seconds and gives up after 5 minutes. Change that with the `timeout` constructor argument or the `Timeout` and `PollEvery` properties. Every method takes a `CancellationToken`.

```csharp
var gifs = new AigifmakerClient(apiKey: "sk-...", timeout: TimeSpan.FromMinutes(10));
```

## Errors worth catching

Every failure is an `AigifmakerException` with a `Kind`; the common ones have their own type:

- `AuthException`: no key, or the key is wrong or deleted
- `QuotaException`: not enough credits left; the message points to the pricing page
- `RejectedException`: the content filter refused the prompt, or the account is cooling down after a refusal; reword rather than retry
- `Kind == Failed`: the GIF was accepted and then failed; the message carries the reason and the credit is refunded
- `Kind == Timeout`: the wait ran out; the GIF may still finish in your history

Questions: support@aigifmaker.ai. MIT licensed.
