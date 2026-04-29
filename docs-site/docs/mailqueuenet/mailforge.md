---
title: MailForge
sidebar_position: 6
---

MailForge is the **mail-merge worker** in the MailQueueNet stack.

It takes a merge template (a `MailMessageWithSettings` containing subject/body/from/etc.) plus batches of JSONL data rows, renders each row into a concrete email, and then queues the resulting emails back into `MailQueueNet.Service` for delivery.

For template authoring help, see:

- [Liquid (Fluid.Core) syntax reference](./liquid-reference.md)
- [Handlebars (Handlebars.Net) syntax reference](./handlebars-reference.md)

## What MailForge does

MailForge is responsible for:

- Accepting merge job dispatch requests from `MailQueueNet.Service`.
- Persisting durable merge-job state so work can survive restarts.
- Rendering templates using supported engines:
  - Liquid
  - Handlebars
- Reading merge-row batches (JSONL) and producing `System.Net.Mail.MailMessage` instances.
- Queueing rendered mail items back to `MailQueueNet.Service` (bulk queueing, with retry).
- Exposing admin/operator endpoints for:
  - listing jobs
  - viewing job progress
  - previewing stored batch rows
  - reading worker logs

## How MailForge relies on `MailQueueNet.Service`

MailForge does **not** send email directly.

Instead:

1. Clients queue a merge template + batch rows into `MailQueueNet.Service`.
2. The queue service writes a **template file** and one or more **batch JSONL files** into its merge queue folder.
3. `MailQueueNet.Service` dispatches the merge job to a MailForge worker via gRPC.
4. MailForge renders each row, and then queues the resulting emails back into `MailQueueNet.Service` using its gRPC API.
5. The queue service persists and delivers those rendered emails using `MailQueueNet.Core` provider implementations.

This separation keeps MailForge focused on merge orchestration, while the queue service remains the single place responsible for durable queueing and outbound delivery.

## Storage: where merge state lives

MailForge uses **durable local storage** for job state and for operator previews.

### Per-job SQLite database

For each merge job, MailForge creates a folder under a configured *job work root* and stores state in:

- `<JobWorkRoot>/<MergeId>/job.db`

This DB tracks:

- job status (Pending/Running/Paused/Completed/Failed/Cancelled)
- counters (total/completed/failed)
- the serialized template (protobuf)
- batch status and progress

This is the primary storage requirement for MailForge reliability.

### Batch-row storage (JSONL)

MailForge also stores batch rows for preview/debugging.

By default, batches are written under:

- `<MailForgeBase>/data/batches`

You can override this folder using the environment variable:

- `MAILFORGE_BATCH_FOLDER`

When a batch is successfully acknowledged with the queue service, MailForge moves the JSONL file into a `processed` subfolder to prevent re-processing while still retaining rows for operator preview.

## Attachments: where they stay and how they are referenced

Mail-merge templates can include attachments.

### Attachment tokens (recommended / remote-safe)

In the MailQueueNet stack, attachments are typically handled as **uploaded attachment tokens**.

- Clients upload attachment bytes to `MailQueueNet.Service`.
- The service stores attachment bytes in its attachment store (see `Attachments:*` settings in the service).
- The client (or service) queues mail items that reference the attachment using `attachment_tokens`.

During mail-merge:

- The template contains `AttachmentTokenRef` entries.
- MailForge copies those token references onto each rendered message.
- When the rendered message is queued back to `MailQueueNet.Service`, the service validates token readiness and increments token reference counts.

This is the normal model when MailForge and MailQueueNet do **not** share a filesystem.

### File-backed attachments

File-backed `System.Net.Mail.Attachment` instances (referenced by file path) only work reliably when the queue service can resolve those paths.

For most distributed deployments, prefer attachment tokens.

## Resilience behaviour

MailForge is designed to cope with both:

- MailForge restarts (merge state is durable in SQLite)
- MailQueueNet service outages (queueing back to the service includes retry + optional disk persistence)

### Dispatch reliability (service → MailForge)

`MailQueueNet.Service` dispatches merge jobs to MailForge workers and maintains a lease/fence token.

- The queue service holds a lease on a specific worker.
- Mutating calls include an `x-dispatch-fence` header.
- MailForge persists the highest accepted fence token in a local SQLite fence database.

This prevents stale or duplicate dispatchers from replaying older merge operations against a worker.

### Queueing reliability (MailForge → service)

When MailForge queues rendered messages back to `MailQueueNet.Service`, it uses a retriable client wrapper that supports:

- retries with exponential backoff
- optional disk resilience (persist undelivered queue requests to disk)

In MailForge this is configured via the `MailQueue` options (see below).

Important note: the merge job can still be marked “completed” in MailForge even if queueing to the service is failing for extended periods, depending on how the underlying queue client is configured. If you need stricter guarantees, ensure disk resilience is enabled and monitor the undelivered folder.

## Configuration

MailForge is configured through standard ASP.NET configuration (appsettings, environment variables, etc.).

### 1) Job work roots (required for durability)

Configure where MailForge stores per-job state (`job.db`).

Recommended keys (bind to `MailForgeOptions`):

- `MailForge:JobWorkRoot` (single root convenience value)
- `MailForge:JobWorkRoots` (list of roots to probe)

Example:

```json
{
  "MailForge": {
    "JobWorkRoot": "/data/mailforge/jobs",
    "JobWorkRoots": [
      "/data/mailforge/jobs"
    ]
  }
}
```

Storage guidance:

- Put the job work root on **durable storage**.
- Ensure the MailForge process identity can read/write.
- In containers, mount a persistent volume for this path.

### 2) Batch folder (optional)

Override the default batch folder using:

- `MAILFORGE_BATCH_FOLDER=/data/mailforge/batches`

### 3) MailQueueNet connection (required)

MailForge needs to be able to call `MailQueueNet.Service` to queue the rendered messages.

These settings bind to `MailQueueOptions`:

- `MailQueue:Address`
- `MailQueue:ClientId`
- `MailQueue:SharedSecret`
- `MailQueue:MaxBatchSize`
- `MailQueue:RetryCount`
- `MailQueue:RetryBackoffFactor`
- `MailQueue:EnableDiskResilience`
- `MailQueue:UndeliveredFolder`

Example:

```json
{
  "MailQueue": {
    "Address": "https://mailqueuenet:5001",
    "ClientId": "mailforge",
    "SharedSecret": "<shared-secret>",

    "MaxBatchSize": 50,
    "RetryCount": 3,
    "RetryBackoffFactor": 2.0,

    "EnableDiskResilience": true,
    "UndeliveredFolder": "/data/mailforge/undelivered"
  }
}
```

If `EnableDiskResilience` is enabled, ensure `UndeliveredFolder` is also on durable storage.

### 4) Admin access (mTLS)

MailForge supports admin-only operations over mTLS client certificates.

Configure the allowlist in:

- `Security:AdminCertThumbprints`

This is used by operator tooling (for example MailFunk dashboards) to inspect job status and logs.
