# The embeddable chat widget

One script tag puts an agent on a page of your application.

```html
<script src="/netcoreai/_content/widget.js" data-agent="support"></script>
```

That is a floating button that opens a panel and streams the agent's answers. The **Embed…** button on
each agent in the dashboard gives you the snippet with the id filled in.

In a Blazor host, the component does the same thing:

```razor
<NetCoreAIChat AgentId="support" Title="Ask support" />
```

## It carries no key, and must not be given one

The widget talks to the host that served it, as the browser, with whatever cookie the visitor already has.
There is no attribute for an API key and there will not be one: **a key in a page is a public key.** Anyone
who can view source can read it, and a key scoped to run an agent is a key that runs that agent for
anybody, at your expense.

That means the visitor's own session decides whether the run is allowed. On a host where the dashboard is
behind a sign-in, only signed-in visitors get answers — which is usually what you want for an internal
assistant. For a **public** assistant, put your own endpoint in front:

```csharp
app.MapPost("/ask", async (AskRequest ask, IAgentClient agents, CancellationToken ct) =>
    Results.Ok(await agents.RunAsync("support", new AgentRequest { Message = ask.Message }, ct)))
   .AllowAnonymous()
   .RequireRateLimiting("public-ask");
```

Your endpoint decides who may ask and how often, which is a decision only you can make. The widget is not
the place for it.

The script itself is fetchable without signing in, because it goes on pages your visitors see — a script
only the dashboard can fetch could only ever appear on the dashboard. Being able to fetch the script is not
being able to run the agent.

## Theming

Everything is drawn through CSS variables on `.netcoreai-widget`, so restyle it from your own stylesheet
and never touch the file:

```css
.netcoreai-widget {
  --ncai-accent: #0f766e;
  --ncai-bg: #fffdf7;
  --ncai-radius: 4px;
  --ncai-width: 420px;
  --ncai-font: "Iowan Old Style", Georgia, serif;
}
```

Also available: `--ncai-accent-text`, `--ncai-text`, `--ncai-muted`, `--ncai-border`, `--ncai-bubble`,
`--ncai-height`. Dark mode follows `prefers-color-scheme` unless you override the variables yourself.

Attributes: `data-title`, `data-greeting`, `data-placement="left"`.

## What it does and does not do

The answer is written as **text, never as markup**. An answer is model output, and a widget that rendered
it as HTML on your page would be a cross-site scripting hole with a friendly face. That also means no
rendered markdown: if you want formatting, the native API gives you the text and your page can render it
with a library you have chosen and can update.

It keeps a session id across turns, so the agent remembers the conversation within the panel. Escape
closes it — a fixed panel with no keyboard way out is a trap.

Citations, tool traces and run ids are not shown. They exist on the run, and a support widget is the wrong
place to put them; use `/api/agents/{id}/run/stream` directly if you want to.

**Cross-origin embedding is not supported out of the box.** The widget expects to be on a page served by
the same host. Putting it on another origin needs CORS configured on your host and a credential story that
is not a cookie, and both of those are decisions about your deployment rather than defaults NetCoreAI
should pick.
