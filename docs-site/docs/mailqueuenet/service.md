---
title: MailQueueNet.Service
sidebar_position: 5
---

`MailQueueNet.Service` is the long-running queue service.

It exposes a gRPC API for clients to queue mail items, persists them to durable storage, and processes them in the background using provider implementations from `MailQueueNet.Core`.

## What the service does

At a high level, the service is responsible for:

- Hosting the gRPC endpoint (HTTP/2 over Kestrel).
- Accepting queue requests (single mail, bulk mail, and mail merge workflows).
- Persisting queued work to disk so that work survives process restarts.
- Running background worker loops that:
  - Load queued items.
  - Attempt delivery.
  - Apply retry rules.
  - Move permanently failed items to a failed folder for later inspection.

## Resilient storage requirements

The service’s reliability depends heavily on the storage used for the queue and failed folders.

### Required characteristics

The queue storage should be:

- **Durable**: survives host restarts and deployment swaps.
- **Writable** by the service identity.
- **Stable and consistent**: avoid transient or eventually-consistent network drives.
- **Backed up** (or at least monitored) if queued messages must not be lost.

### Recommended deployment patterns

- **Windows service / VM**: use a dedicated local volume (or a persistent attached disk) for the queue folders.
- **Containers**: mount persistent volumes; avoid container filesystem for queue state.
- **Multi-instance**: prefer a single service instance per queue folder unless you have verified the shared filesystem semantics and locking behaviour for your environment.

### Folder separation

Keep these folders separate:

- Queue folder: high churn, frequent writes.
- Failed folder: lower churn, retained for investigation.
- Mail merge queue folder: batch workflows and intermediate artefacts.

This makes it easier to monitor growth and apply different retention policies.

## Configuration

The primary configuration lives under the `queue` section.

### Queue folders and worker behaviour

| Key | Purpose |
| --- | --- |
| `queue:queue_folder` | Folder where queued mail items are persisted. |
| `queue:failed_folder` | Folder where permanently failed mail items are moved for investigation. |
| `queue:mail_merge_queue_folder` | Folder for mail merge batch work. |
| `queue:seconds_until_folder_refresh` | Polling interval for scanning queue folders. |
| `queue:maximum_concurrent_workers` | Maximum concurrent background workers processing the queue. |
| `queue:maximum_failure_retries` | Maximum delivery attempts before a message is moved to the failed folder. |

Example:

```json
{
  "queue": {
    "queue_folder": "D:/mail/queue",
    "failed_folder": "D:/mail/failed",
    "mail_merge_queue_folder": "D:/mail/merge",
    "seconds_until_folder_refresh": 10,
    "maximum_concurrent_workers": 4,
    "maximum_failure_retries": 5
  }
}
```

### Provider selection (powered by `MailQueueNet.Core`)

`MailQueueNet.Service` relies on `MailQueueNet.Core` to perform the actual delivery.

Select the provider using:

- `queue:mail_service_type`

Supported values in this repository:

- `smtp`
- `mailgun`

SMTP settings:

```json
{
  "queue": {
    "mail_service_type": "smtp",
    "smtp": {
      "server": "localhost",
      "port": 1025,
      "ssl": false,
      "authentication": false,
      "username": "",
      "password": "",
      "connection_timeout": 100000
    }
  }
}
```

Mailgun settings:

```json
{
  "queue": {
    "mail_service_type": "mailgun",
    "mailgun": {
      "domain": "example.mailgun.org",
      "api_key": "<api-key>",
      "connection_timeout": 100000
    }
  }
}
```

### Hosting (Kestrel)

The service uses Kestrel endpoints configuration.

Example:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5000"
      },
      "Https": {
        "Url": "https://0.0.0.0:5001"
      }
    },
    "EndpointDefaults": {
      "Protocols": "Http1AndHttp2"
    }
  }
}
```

gRPC requires HTTP/2. The default configuration enables `Http1AndHttp2` to support both gRPC and non-gRPC endpoints.

### Security (client + admin)

The service supports client authentication and admin authorisation.

Configuration keys used in `appsettings.json`:

- `Security:SharedClientSecret`
- `Security:AdminCertThumbprints`

These values are used by the gRPC client helpers in `MailQueueNet.Common` (for example `ConfigureClientAuth(...)`) and by admin workflows.

## Staging mail routing allow-list

When `MailQueueNet.Service` is running in the `Staging` environment, it can be configured to route mail safely:

- a full copy is sent to Mailpit by default
- a filtered copy can also be sent through the real SMTP server for allow-listed recipients
- allow-listed recipients are stored per authenticated client id
- the allow-list management gRPC endpoints use client shared-secret authentication rather than admin authentication

### Behaviour

For a staging message:

- the original message is sent to Mailpit
- if any `To`, `Cc`, or `Bcc` recipients are allow-listed for the client id, a second copy is sent via the configured real SMTP server
- the real SMTP copy strips all non-allow-listed recipients
- the real SMTP copy includes a configurable subject prefix such as `[STAGING] `

If no recipients are allow-listed, only the Mailpit copy is sent.

This routing also applies when callers provide custom SMTP settings. Staging safety still wins unless the recipient is allow-listed.

### Configuration

Example staging configuration:

```json
{
  "StagingMailRouting": {
    "Enabled": true,
    "ForceMailpitOnly": false,
    "SubjectPrefix": "[STAGING] ",
    "Mailpit": {
      "Host": "mailpit.dev.internal.ibc.com.au",
      "Port": 1025,
      "RequiresSsl": false,
      "RequiresAuthentication": false,
      "Username": "",
      "Password": "",
      "ConnectionTimeout": 100000
    },
    "RealSmtp": {
      "Host": "smtp.example.internal",
      "Port": 25,
      "RequiresSsl": false,
      "RequiresAuthentication": false,
      "Username": "",
      "Password": "",
      "ConnectionTimeout": 100000
    }
  }
}
```

### Client-authenticated allow-list management

The following gRPC endpoints are intended for staging and operate on the authenticated client's own allow-list:

- `ListAllowedTestRecipients`
- `AddAllowedTestRecipient`
- `DeleteAllowedTestRecipient`

The caller must authenticate with the existing client shared-secret headers:

- `x-client-id`
- `x-client-pass`

The server does not allow the caller to manage another client's allow-list through request payload values.

MailFunk can also manage any client's allow-list when it calls these endpoints with admin authentication. In that case the request supplies `client_id`, and the service applies the change to that target client.

## Relationship to client-side resilience

The service provides durable queueing and server-side retries via `queue:maximum_failure_retries`.

If you also need resilience on the *client* side (for example your client app must cope with the service being offline for hours), use the `MailQueueNet.Common` resilient client wrapper:

- `MailGrpcServiceClientWithRetry`

That wrapper adds Polly retries, disk persistence of outbound queue requests, and a background resend loop.
