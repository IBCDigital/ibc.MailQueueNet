---
title: MailFunk
sidebar_position: 7
---

MailFunk is the **Blazor Server operator UI** for the MailQueueNet stack.

It is intended for operators and administrators to monitor queue health, inspect queued/failed mail, manage retries, view attachments, manage server settings, and inspect MailForge merge activity.

## What MailFunk is for

Use MailFunk when you need to:

- Monitor whether the queue service is reachable and processing.
- Pause and resume queue processing.
- Inspect queued and failed mail items.
- Retry failed mail items.
- Delete queued/failed mail items in bulk.
- View and download uploaded attachments (attachment tokens).
- View and change live service settings (no restart required).
- Inspect MailForge merge activity (jobs, progress, batch previews).
- Read logs from MailFunk itself, the queue service, and MailForge.
- Generate client credentials headers for integrating new applications.
- Send test mail and test merge batches end-to-end.

## Setup

### Service endpoints

MailFunk calls gRPC endpoints on:

- `MailQueueNet.Service` (required)
- `MailForge` (optional but recommended for merge dashboards)

Configure these in `appsettings.json` or environment variables:

- `MailService:BaseAddress` (for example `https://mailqueuenet:5001`)
- `MailForge:BaseAddress` (for example `https://mailforge:5003`)

Example:

```json
{
  "MailService": {
    "BaseAddress": "https://localhost:5001"
  },
  "MailForge": {
    "BaseAddress": "https://localhost:5003"
  }
}
```

### TLS and certificates

MailFunk is a Blazor Server app (ASP.NET). In development it typically runs with a local development certificate. For production deployments, configure Kestrel TLS as per your hosting environment.

MailFunk can also present an **admin client certificate** when calling gRPC services, enabling admin-only operations (settings mutation, attachment administration, MailForge admin endpoints).

Admin client certificate configuration:

- `AdminClientCert:Thumbprint` (load from current user certificate store), or
- `AdminClientCert:Path` + `AdminClientCert:Password` (load from PFX file)

Example:

```json
{
  "AdminClientCert": {
    "Thumbprint": "<thumbprint>",
    "Path": "../Certs/AdminAccess.pfx",
    "Password": "<pfx-password>"
  }
}
```

Important:

- The certificate must include a private key, otherwise it cannot be presented for mTLS.
- The service-side allowlist must include the certificate thumbprint:
  - `MailQueueNet.Service`: `Security:AdminCertThumbprints`
  - `MailForge`: `Security:AdminCertThumbprints`

## Security

MailFunk applies two layers of security:

### 1) Operator sign-in (who can access the UI)

MailFunk uses a “smart” authentication scheme:

- **Windows Integrated Authentication (Negotiate)** when available.
- **OpenID Connect (Entra ID / Azure AD)** as the interactive fallback.

Configuration keys:

- `AzureAd:TenantId`
- `AzureAd:ClientId`
- `AzureAd:ClientSecret`

For environment setup and operational steps, see:

- [MailFunk Microsoft SSO (Entra ID)](./mailfunk-sso.md)

Sign-in and sign-out endpoints:

- `/signin`
- `/signout`

Operational expectation:

- Do not expose MailFunk publicly without identity controls.
- Restrict access at the network layer as appropriate.

### 2) Admin certificate (what the UI is allowed to do)

Some actions require the gRPC services to treat the caller as an admin.

MailFunk can be run in a read-only mode (no admin client cert configured), but features such as **editing server settings** or performing **attachment administration** may fail with authorisation errors.

## Using the interface

MailFunk is organised into pages that map to common operator workflows.

### Dashboard (`/`)

The dashboard provides an at-a-glance view of:

- Current processing state (Processing vs Paused).
- Usage summaries (most active client, queued/sent/failed counts for a selected time window).
- Mail merge activity summaries (active and recent merges).
- Merge dispatcher state rows (which worker was leased, fence token, last error).
- Mail folders:
  - Queue
  - Failed

Key actions:

- **Refresh**: reloads all dashboard data.
- **Pause / Resume**: pauses queue processing, or resumes it.
  - When pausing, you can specify a pause duration (minutes).
- **Folder panels**:
  - Select mail items to preview.
  - Load more items (paging).
  - Perform bulk actions (delete; and retry from Failed).

### Server Settings (`/settings`)

Use this page to view and change live settings on `MailQueueNet.Service`.

Features:

- View current settings (queue folders, worker limits, retry limits, pause limits).
- View current mail provider settings (SMTP or Mailgun).
- Edit and save settings.
- Copy the settings payload as JSON for auditing or change management.

Notes:

- Settings are applied immediately by the service; a restart is not required.
- Saving settings typically requires admin privileges on the queue service.

### Attachments (`/attachments`)

Use this page to inspect and manage uploaded attachments (attachment tokens) stored by the queue service.

Features:

- Filter by:
  - Client id
  - Merge id (merge owner id)
  - orphaned tokens (ref count = 0)
  - large tokens (threshold in MB)
  - older-than date
- View token metadata:
  - token
  - upload time
  - size
  - reference count
  - readiness
  - client / merge id
- Actions per token:
  - View manifest
  - Download
  - Delete

Notes:

- Deleting a referenced token can break mail items that depend on it.
- Attachment administration requires admin privileges on the queue service.

### Logs (`/logs`)

Use this page to inspect logs from:

- MailFunk (local file logs)
- MailQueueNet.Service (remote via gRPC)
- MailForge (remote via gRPC)

Features:

- Select a source.
- List log files.
- Load and view content.
- Enable live mode (streaming) for remote sources.

Notes:

- Remote log access requires admin privileges on the corresponding service.

### MailForge dashboards (`/mailforge` and `/mailforge/{MergeId}`)

These pages provide operator visibility into MailForge merge jobs.

`/mailforge` features:

- List recent merge jobs.
- Preview the selected job:
  - template content
  - merge rows (formatted or raw)
  - per-batch status and counters

`/mailforge/{MergeId}` features:

- View a specific merge job.
- Pause / Resume a running job.
- Delete a job and stored state.
- Preview batch rows.

Notes:

- These endpoints typically require that MailFunk can reach MailForge.
- Admin operations require MailForge admin privileges (mTLS certificate allowlist).

### Client credentials generator (`/client-generator`)

This page helps operators generate the shared-secret client authentication header used by clients.

Features:

- Enter `Client ID` and the shared secret.
- Compute the `x-client-pass` header.
- Copy sample headers.

Notes:

- This is a convenience tool; treat the shared secret as sensitive.

### Test Mail (`/test-mail`)

Use this page to validate end-to-end mail queueing.

Features:

- Send a standard mail message:
  - configure `x-client-id` and `x-client-pass`
  - set from/to/cc/bcc/subject/body
  - upload attachments
- Send a merge mail batch:
  - create a new merge or append to an existing merge id
  - select template engine (Liquid or Handlebars)
  - upload JSONL rows
  - upload template attachments

Notes:

- For remote services, attachments are uploaded and converted into attachment tokens.
- Merge batches will only be processed if the queue service is configured to dispatch to MailForge.
