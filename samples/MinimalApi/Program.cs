// The smallest possible NetCoreAI host: an existing minimal API plus two lines.
using Microsoft.Extensions.AI;
using NetCoreAI;

var builder = WebApplication.CreateBuilder(args);

// Your existing app registrations go here...

// 1. Register NetCoreAI (options are bound from the "NetCoreAI" configuration section, overridable in code).
builder.Services.AddNetCoreAI(o =>
{
    o.DataDirectory = "./netcoreai";
    o.Dashboard.AllowAnonymous = true;   // sample only: no auth in this app. Set an authorization policy in real hosts.
})
// A local GGUF file needs a backend that can read one. Without this the framework still runs, but
// importing a .gguf from the Model Hub fails: nothing registered recognises the format.
.AddGgufBackend()
.AddOnnxBackend()
.AddOllamaBackend()
.AddOpenAICompatibleBackend()
.AddAnthropicBackend();

var app = builder.Build();

// Your existing endpoints keep working untouched.
app.MapGet("/", () => "Hello from the host app. Dashboard at /netcoreai");

// Use a model from your own code via Microsoft.Extensions.AI: no provider-specific types.
app.MapGet("/summarize", async (string text, IChatClientFactory chat, CancellationToken ct) =>
{
    IChatClient client = chat.Get("default");
    var response = await client.GetResponseAsync($"Summarize in one sentence:\n{text}", cancellationToken: ct);
    return Results.Text(response.Text);
});

// 2. Mount the dashboard, management API and agent API at /netcoreai.
app.MapNetCoreAI();

app.Run();
