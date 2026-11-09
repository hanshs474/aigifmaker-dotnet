// Two live checks against https://aigifmaker.ai that spend nothing and need no real key:
// a wrong key on credits(), and a submit with no key at all (the service answers with a wall).
// dotnet run --project examples/Smoke
using Aigifmaker;

var ok = true;
try
{
    await new AigifmakerClient("sk-invalidtest000").CreditsAsync();
    Console.WriteLine("FAIL wrong key accepted");
    ok = false;
}
catch (AuthException e) { Console.WriteLine($"ok   wrong key -> AuthException: {e.Message}"); }

try
{
    await new AigifmakerClient("").SubmitAsync("a cat waving hello");
    Console.WriteLine("FAIL submit without a key accepted");
    ok = false;
}
catch (AuthException e) { Console.WriteLine($"ok   no key -> AuthException: {e.Message}"); }

return ok ? 0 : 1;
