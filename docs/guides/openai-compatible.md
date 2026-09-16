# The OpenAI-compatible endpoint

So that a tool already pointed at OpenAI can be pointed at your host instead by changing a base URL and a
key.

```python
from openai import OpenAI

client = OpenAI(
    base_url="https://myapp.example.com/netcoreai/v1",
    api_key="ncai_...",          # a NetCoreAI API key
)

client.chat.completions.create(
    model="support",             # a model, an alias, or an agent
    messages=[{"role": "user", "content": "where is my order A-7?"}],
)
```

Three endpoints: `POST /v1/chat/completions` (streaming and not), `POST /v1/embeddings`, and
`GET /v1/models`.

## `model` can be an agent

This is the useful part. Name an agent and the agent runs — its system prompt, its tools, its knowledge
bases, its guardrails — and the client neither knows nor needs to. A support chatbot written against
OpenAI becomes a support chatbot with access to your order system, with no change beyond the model name.

`GET /v1/models` lists models as `owned_by: netcoreai-model` and agents as `netcoreai-agent`, so a client
that builds a model picker from it shows both.

**Only the last user message is taken as the question.** Most clients resend the whole conversation on
every turn, and an agent keeps its own memory; sending all of it would count the conversation twice.

## What it is, and is not

A translation layer, not a second API. Everything NetCoreAI can do that this shape cannot express —
citations, tool traces, run ids, retrieved passages — stays on the native API at `/api`, and nothing here
invents a field to carry it. If you want those, use `/api/agents/{id}/run`.

Not implemented: function calling through this endpoint (agents call their own tools, which is the point),
`n > 1`, logprobs, and vision. Text parts of a multi-part message are read and image parts are dropped
rather than refused — a host with no vision model cannot do anything useful with an image, and answering
the text beats rejecting the message.

Fields this host cannot honour — `frequency_penalty`, `logit_bias`, `seed`, `user` — are **ignored, not
refused**. Clients send them to everybody, and a 400 about a field the model has no notion of would make
the compatibility layer useless for exactly the clients it exists for.

## Errors

In OpenAI's envelope, because a client written against OpenAI reads `error.message`:

```json
{ "error": { "message": "The model 'no-such-model' does not exist.", "type": "invalid_request_error", "param": null, "code": "model_not_found" } }
```

Not an RFC 9110 problem document like the rest of the API. Giving one here would mean every OpenAI client
reports "an error occurred".

An error that happens *mid-stream* arrives as a chunk with `finish_reason: "error"` followed by `[DONE]`.
By then the status is already 200, so the honest thing left is to say so in the stream: a client sees a
truncated answer with a reason rather than one that stops for no stated reason.

## Authorization and tenancy

The endpoint sits under the same route group as the dashboard, so it inherits the same authorization
policy and the same tenant resolution. An API key scoped to particular agents may only name those agents
as the model; naming another gets a 403 in the envelope above.

Runs made through here are recorded like any other — the same traces, the same usage figures, the same
audit entries.
