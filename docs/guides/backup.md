# Backup, and moving work between hosts

Two different jobs, and conflating them is how people end up with neither.

- **A bundle** moves what a person *built* — agents, tools, knowledge base definitions — from one host to
  another. Dev to production. A file you commit.
- **A backup** takes a copy of what a host *has*, so you can put it back after something goes wrong.

## Bundles

```
GET  /api/bundles/export?agents=support,triage&description=Release%2012
POST /api/bundles/import?mode=Validate      // then SkipExisting, or Overwrite
```

Naming agents brings the tools they call and the knowledge bases they read; naming none exports
everything. Exporting the whole host when somebody asked for one agent hands them a file full of things
they did not mean to move.

**What travels:** agents, tools, knowledge base definitions, their data sources, model aliases, and the
*names* of the provider connections the contents expect.

**What does not:** documents, vectors, conversations, run traces, audit entries. Those belong to the
environment they happened in.

**Secrets never travel.** A connection is carried as an id, a provider and a base URL — never its key. A
bundle is a file people email each other and commit to repositories, and it is designed on the assumption
that they will. After importing, create the connections it names and give each one its own secret; the
import report tells you which.

### Import in two passes

`mode=Validate` writes nothing and tells you what would happen. Run it first. Then `SkipExisting` (import
what is new, leave what exists) or `Overwrite` (replace).

The part worth reading is `missing`:

```json
{ "missing": [
  "agent support needs tool 'lookup_order'",
  "agent support needs model or alias 'gpt-4o'",
  "connection 'main' (openai) is not set up here — create it and give it its own secret"
] }
```

This is the whole point of the report. An agent that arrives without its tools imports cleanly, runs,
answers, and is quietly wrong — for weeks. Being told at import time costs a minute.

**Imported tools arrive without permission to run in-process.** Running inside this host's process is an
act by a named administrator *here*, not something a file carries across from somewhere else. Re-grant it
on the Tools page if you want it.

## Backups

```
POST /api/backup
```

Writes a consistent copy of the metadata database to `{DataDirectory}/backups/netcoreai-<timestamp>.db`,
while the host is running.

It uses SQLite's `VACUUM INTO` rather than copying the file. **Copying a live SQLite file is how you get a
backup that restores into a corrupt database** — the copy catches it mid-transaction, and the `-wal` file
beside it holds writes the main file does not, so copying the main file alone also loses whatever happened
most recently. `VACUUM INTO` takes a read lock rather than blocking writers, and compacts on the way out.

It refuses to write over an existing file. Silently replacing yesterday's backup with today's is how
somebody ends up with exactly one backup, taken after the thing they needed to recover from.

### What a backup does and does not cover

| | |
|---|---|
| Metadata (models, agents, tools, KBs, settings, keys, runs, audit) | `POST /api/backup` |
| Vector data (`vectors.db`) | **not covered — stop the host and copy it** |
| Model files and uploaded documents | not covered; they are files, copy the directory |

**Restore is a manual step and there is no endpoint for it.** Stop the host, put the file back as
`{DataDirectory}/netcoreai.db`, delete any `-wal` and `-shm` beside it, start the host. A restore endpoint
would have to replace the database the running host is reading from, which is not something to do over
HTTP while it is serving requests.

Vector-store backup and a supported restore path are outstanding. Until then: for a complete backup, stop
the host and copy the whole data directory. That is slower and correct, which is the right way round for
a backup.
