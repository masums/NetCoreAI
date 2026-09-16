# Guardrails

What an agent will not do. Every rule here is off until you turn it on: a guardrail that fires when nobody asked for it turns a working agent into a broken one, and you are the only party who knows which rules your users can live with.

Rules live on the agent (`AgentDefinition.Guardrails`) or, for every agent that carries none of its own, on the host (`NetCoreAI:Guardrails`). They are edited on the Agents page, or set in code:

```csharp
await agents.SaveAsync(agent with
{
    Guardrails = new GuardrailPolicy
    {
        Content = new ContentPolicy { MaxInputCharacters = 4000 },
        Pii = new PiiPolicy { InputAction = GuardrailAction.Mask, OutputAction = GuardrailAction.Mask },
        Injection = new InjectionPolicy { Enabled = true },
        Budget = new BudgetPolicy { MaxTokensPerSession = 20_000, MaxCostPerDay = 25m },
    },
});
```

## Four things a rule can do

| Action | What happens |
|---|---|
| `Ignore` | Nothing is looked at. The default, and genuinely free — no scanning, no trace entries. |
| `Report` | Noticed and written into the run trace. Start here. |
| `Mask` | What matched is replaced, and the run carries on. |
| `Block` | The run stops and the caller is told, in your words. |

`Report` first is not politeness, it is the only way to find out what a rule would have refused. Every finding lands in the run trace as a `guardrail` step whichever action you pick, so you can read a week of them on the Runs page before switching one to `Block`.

## Content rules

A maximum length, a list of refused phrases, and a list of refused regular expressions. Checked **before** retrieval and before any model is called — which matters when the model is somebody else's, because a refusal issued after the call has already sent the message and already been paid for.

A pattern that does not compile is logged and skipped rather than thrown, so a stray bracket does not take down every run of the agent. A pattern that *times out* against a particular message is treated as a match: the safe reading of "I could not check" is not "it was fine".

The caller is told your `BlockedMessage`, not which rule fired. Naming the rule tells somebody probing it exactly which words to avoid next time.

## Personal data

Regular expressions for email addresses, phone numbers, card numbers (confirmed by the Luhn check, which is what separates a card from an order reference), national ids, IBANs, IP addresses, and credential shapes — bearer tokens, private key blocks, `sk-`/`ghp_`/`AKIA`/`xox…` prefixes.

**This finds the shapes people write things in and nothing else.** It will miss a name, a postal address, and anything written unusually. It reduces what leaves your process; it does not tell you what is left. Anyone treating it as a compliance control should know that before they do.

Masking replaces a match with a named placeholder — `[email address]`, `[card number]` — rather than blanking it, so the model can still write a sentence: "we will email you at [email address]" reads as an answer where a row of asterisks reads as a fault.

Output masking works on a streaming answer, which takes some care: a card number arrives as several deltas, and a rule applied to each delta alone sees four ordinary digits four times and passes all of them. So text is held back until enough has arrived for a match to be impossible across the seam, then masked and released. The caller sees the answer a fraction of a second later; that is the price of the rule working at all. With output masking off, deltas pass straight through and nothing is held.

On the way out, `Block` behaves as `Mask`. By the time an answer exists the caller has usually seen the start of it.

## Prompt injection

Heuristics, and nothing more. Injection is an intent, not a pattern, and intent is expressed in a natural language with unlimited paraphrase — anything here can be worked around by somebody who reads this page. The value is that most attempts are not careful.

Five signals: asking the agent to ignore its instructions, asking it to reveal them, conversation role markers written into the message, an attempt to give it a different persona, and asking it to bypass its restrictions. The default threshold is **two**, because ordinary questions trip one — "what are the rules for expense claims?" is a real question that reads like an extraction attempt — and an agent that refuses those is worse than an agent with no heuristics at all.

`Mask` here does not remove anything, because there is nothing reliable to remove. It prefixes the message with a line telling the model to treat what follows as information rather than instructions.

**Nothing in NetCoreAI relies on these for its safety.** What a tool will do is decided by the tool's own rules and the caller's own identity — a tool call runs as the caller, and an ACL-restricted document stays restricted — not by whether the message asking for it looked suspicious. Treat this as a tripwire, not a wall.

## Tools by role

```csharp
ToolsByRole = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
{
    ["support"] = ["lookup_order"],
    ["finance"] = ["lookup_order", "issue_refund"],
}
```

A tool this caller may not use is never put in front of the model, rather than offered and refused on use — a model told about a tool will spend a call trying it and read the refusal as a fault to work around.

Two rules worth knowing:

- **Holding two listed roles grants both lists**, never their intersection. An extra role must never leave somebody with less.
- **A caller in none of the listed roles keeps the agent's own tools.** Reading it the other way would mean that adding one role's allow-list silently disarmed the agent for everybody else, which is not what writing one down says. If you want a default, name the role that has it.

## Budgets

Tokens per answer, per conversation, per person per day, and estimated spend per agent per day.

The per-answer limit is applied as a cap on what the model is asked for, not as a check afterwards: a budget only enforced once the tokens are spent is a report. The others are checked before the run starts, so an exhausted budget costs nothing.

**Counted in this process only** — the same bargain as the API key rate limiter. Behind a load balancer, *n* instances allow *n* times the budget, and a restart clears the totals. This is a bound on a runaway loop, not an accounting boundary. For real accounting, use the run traces: they carry tokens and cost per run and survive a restart.

A limit of `0` means no limit, not a limit of nothing.

## Where findings go

- **The run trace** — one `guardrail` step per finding, visible on the Runs page next to the retrieval and tool steps. A refusal is recorded as a run like any other: a rule nobody can look up afterwards is a rule nobody can tune, and the first question asked about one is always "what did they actually send?"
- **`netcoreai.guardrail.blocks`** — a counter, tagged by agent. Worth an alert. A rule that fires constantly is usually wrong rather than usually right.

Guardrail steps carry what matched by rule name and a short detail, never the matched text itself.
