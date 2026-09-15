# Publishing an agent

The problem: somebody edits a live agent, and finds out in production.

## Until you publish, the draft is what runs

An agent nobody has published runs exactly as it is edited. That is what every agent did before versioning
existed, and it is what a draft should do while you are building one.

**Publishing once changes that for good.** From then on the agent you edit is the draft, and runs use the
published version until you publish again. There is no way back to the old behaviour, deliberately: an
agent that sometimes serves its draft and sometimes does not would be worse than either rule on its own.

Opting in is the act of publishing rather than a setting, because a flag nobody finds is a feature nobody
has.

```
POST /api/agents/support/publish        { "note": "Shipped the refund wording." }
GET  /api/agents/support/versions
POST /api/agents/support/rollback/3     { "note": "The new prompt was answering the wrong question." }
```

The Agents page carries a **Versions…** button, and tags each agent `v4` or `draft` so which one is which
is visible without opening anything.

## Rollback goes forward

Rolling back to version 3 publishes version 3's definition again **as version 5**. Nothing is deleted.

The history is a record of what happened, not a record of what you would now prefer to have happened — and
the rollback is itself a thing that happened, usually the most interesting one. The new version records
which one it restored.

The draft comes with it. Somebody reaching for rollback wants the editor to show what is now serving, not
the change they just undid.

## Two deliberate exceptions

**`Enabled` is read from the draft, not the published version.** Switching an agent off takes effect
immediately, with no publish. Having to publish in order to *stop* something is the wrong way round in an
incident.

**A published version missing from its history is refused, not served from the draft.** If the agent says
it is published at version 4 and version 4 is gone, runs fail with a sentence saying so. Silently serving
the draft is the one thing publishing promised would not happen.

## What a version contains

The whole agent as it was: prompt, model, parameters, tools, knowledge bases, guardrails, limits, access
tags. Plus the note, who published it, when, and the version it rolled back from.

Deleting an agent deletes its history too — otherwise a new agent reusing the id inherits a stranger's
past, and starts out published.

## Moving between environments

Versioning is within one host. To move an agent to another, export a
[bundle](backup.md) — and publish it there, because a freshly imported agent
arrives unpublished and runs as its draft until somebody does.
