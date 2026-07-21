---
title: MailQueueNet.Common (client library)
sidebar_position: 3
---

`MailQueueNet.Common` is the client-facing contract and helper library for MailQueue.net.

It is designed to be referenced by *client applications* that need to queue mail to a MailQueueNet service, without needing to take a dependency on the service host or internal delivery implementation.

## What `MailQueueNet.Common` provides

### 1) gRPC contracts (the public API)

- `.proto` definitions (in the repository `proto/` folder)
- Generated gRPC client/server types (for example `MailQueueNet.Grpc.MailGrpcService`)
- Protobuf message types used as request/response DTOs

Treat the gRPC contract as **public API surface area**: changes can impact every client.

### 2) Client convenience helpers for `System.Net.Mail.MailMessage`

Most client apps already know how to build a `System.Net.Mail.MailMessage`. `MailQueueNet.Common` includes helper methods so you do not need to construct protobuf messages directly.

Key helpers include:

- `MailQueueNet.Grpc.MailGrpcService.MailGrpcServiceClient.QueueMail(...)` and `QueueMailReplyAsync(...)`
- `MailQueueNet.Grpc.MailMessage.FromMessage(...)` (converts `MailMessage` â†’ protobuf)
- Attachment token helpers that can upload attachments when the service is remote

### 3) Resilience helpers for long outages

`MailQueueNet.Common` includes a wrapper client (`MailGrpcServiceClientWithRetry`) that adds:

- **Polly retries** with exponential back-off for transient gRPC failures
- **Disk resilience** that persists messages to an `UndeliveredFolder` when all retries fail
- A **background resend worker** that periodically scans `UndeliveredFolder` and retries queueing
- A **lock-file gate** to avoid multiple processes/nodes resending the same messages in clustered deployments

This is intended for scenarios where the queue service may be unavailable for extended periods (maintenance windows, network partitions, outage recovery).
## First integration checklist

If this is your first MailQueueNet client integration, start with the [Client Integration Guide](./client-integration-guide.md). It covers the application wrapper pattern, environment-specific configuration, when not to pass SMTP settings, staging pass-through allow-list sync, and operational UI recommendations.

## Basic client usage example

This example queues a single mail item using the gRPC client helper overloads.

```csharp
using System.Net.Mail;
using Grpc.Net.Client;
using MailQueueNet.Grpc;

var serviceAddress = "https://localhost:5001";

// Used by attachment helpers to decide whether attachments must be uploaded.
MailClientConfiguration.Current = new MailClientConfiguration
{
    MailQueueNetServiceChannelAddress = serviceAddress,
};

using var channel = GrpcChannel.ForAddress(serviceAddress);
var client = new MailGrpcService.MailGrpcServiceClient(channel);

// Optional: if your service expects client auth headers.
MailGrpcService.MailGrpcServiceClient.ConfigureClientAuth(
    clientIdValue: "my-client",
    sharedSecretValue: "shared-secret");

using var message = new MailMessage
{
    From = new MailAddress("no-reply@mailqueue.net"),
    Subject = "Hello",
    Body = "Queued via MailQueueNet",
    IsBodyHtml = false,
};

message.To.Add("someone@example.com");

var reply = await client.QueueMailReplyAsync(message);
if (!reply.Success)
{
    throw new InvalidOperationException(reply.Message);
}
```

### Attachments (local service vs remote service)

MailQueueNet supports attachments, but the handling differs depending on whether the queue service shares a filesystem with the client:

- **Local service** (`localhost`/loopback): attachments may be sent as file-backed references.
- **Remote service**: the client must upload the attachment bytes and send the server an *attachment token* reference.

`MailQueueNet.Common` can do this for you. The `QueueMail...` helper overloads will upload file-backed attachments automatically when the configured service address is remote.

If you need explicit control, you can call:

- `EnsureAttachmentTokensAsync(...)`
- `UploadFileAttachmentAsync(...)`
- `UploadAttachmentsAndApplyTokensAsync(...)`

Important constraints:

- Attachment helpers only support **file-backed** `System.Net.Mail.Attachment` instances.
- If you create attachments from an in-memory stream, write them to a temporary file first.

## Retries + resilience for extended outages

### When to use `MailGrpcServiceClientWithRetry`

Use the resilient wrapper when:

- Your client app must not block critical workflows when the queue service is down.
- You want *bounded* retries (to ride out brief outages) and *durable persistence* for longer outages.
- You want a background resend loop to â€œself-healâ€ once the service is reachable again.

### Example: queue with retry + disk persistence

```csharp
using System.Net.Mail;
using Grpc.Net.Client;
using MailQueueNet.Grpc;
using Microsoft.Extensions.Logging;

var config = new MailClientConfiguration
{
    MailQueueNetServiceChannelAddress = "https://mailqueue.net",

    EnableDiskResilience = true,
    UndeliveredFolder = @"C:\mailqueue\undelivered",

    RetryCount = 5,
    RetryBackoffFactor = 2.0,

    UnsentCheckIntervalMinutes = 10,
    ResendWindowHours = 72,

    // Cluster safety (shared folder recommended if multiple instances may run).
    DistributedLockTimeoutSeconds = 300,
    LockFileName = ".resend.lock",

    // Optional: send an alert when items remain unsent beyond the resend window.
    AlertEmailAddress = "ops@example.com",
    SmtpHost = "smtp.example.com",
    SmtpPort = 587,
    SmtpEnableSsl = true,
    SmtpUsername = "smtp-user",
    SmtpPassword = "smtp-password",
};

MailClientConfiguration.Current = config;

// Required when the queue service has Security:SharedClientSecret configured.
// The resilient wrapper, including its background resend loop, uses these
// credentials for QueueMail, bulk queueing, and mail-merge queueing calls.
MailGrpcService.MailGrpcServiceClient.ConfigureClientAuth(
    clientIdValue: "my-client",
    sharedSecretValue: "shared-secret");

using var channel = GrpcChannel.ForAddress(config.MailQueueNetServiceChannelAddress);
var grpcClient = new MailGrpcService.MailGrpcServiceClient(channel);

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = loggerFactory.CreateLogger<MailGrpcServiceClientWithRetry>();

var resilientClient = new MailGrpcServiceClientWithRetry(grpcClient, config, logger);

using var message = new MailMessage
{
    From = new MailAddress("no-reply@mailqueue.net"),
    Subject = "Resilient queue example",
    Body = "This will be written to disk if the service is offline.",
};

message.To.Add("someone@example.com");

await resilientClient.QueueMailWithRetryAndResilienceAsync(message);
```

### How the resilience loop behaves

When disk resilience is enabled:

1. The client performs gRPC retries (Polly `WaitAndRetryAsync`) for transient errors.
2. If queueing still fails, the message is written to disk in `UndeliveredFolder` using a two-phase write.
3. The client writes the complete protobuf message to a same-folder temporary file named like `*.mail.writing-*.tmp`, flushes it to storage, closes the stream, then publishes it with a same-directory move to the final `*.mail` name.
4. A background timer periodically scans only final `*.mail` files and attempts to resend using the same configured client authentication headers.
5. When a resend succeeds, the `*.mail` file is deleted.
6. If some items remain unsent (for example they are older than `ResendWindowHours`), the client can send an alert email (if SMTP settings are configured).

Temporary files are not resendable mail. Normal resend scans ignore `*.mail.writing-*.tmp`, `*.tmp`, and any non-final extension. If a process is terminated during a write, an abandoned temporary file may remain, but it is not parsed or resent by the normal retry loop. Only completed final `*.mail` files are treated as retry candidates.

### Shared resend lock behaviour

When multiple clients share one `UndeliveredFolder`, the resend loop uses the configured lock file, `LockFileName` (`.resend.lock` by default), to prevent duplicate concurrent resend processing. The lock file is created in the same folder as the retry files and is held with an exclusive file handle for the full resend loop.

When a client acquires the lock it writes safe operational metadata to the lock file:

```plaintext
owner={client-or-node-id}
processId={process-id}
createdUtc={round-trip-utc}
expiresUtc={round-trip-utc}
package=MailQueueNet
state={active-or-released}
releasedUtc={round-trip-utc-if-released}
```

The metadata is for health checks and operational diagnostics only. It must not contain mail payload data, recipient addresses, subjects, message bodies, attachment names, SMTP credentials, shared secrets, or serialized mail content.

If another active owner holds `.resend.lock`, the client skips the resend loop and leaves pending final `*.mail` files untouched. This is expected in multi-node deployments and does not mean the retry files are corrupt. The owning client renews `expiresUtc` while it still holds the lock. If stale metadata exists but exclusive access cannot be acquired safely, MailQueueNet leaves the lock file untouched and skips processing rather than risking duplicate resend. If the owner process terminates and the shared filesystem releases the handle, another client can acquire the lock, replace the metadata, and continue processing.

### Graceful shutdown behaviour

`MailGrpcServiceClientWithRetry` tracks messages while resilient queue calls are in progress.
During a graceful application shutdown, call `StopAsync(...)` or `FlushInFlightToUndeliveredFolderAsync(...)` so in-flight retry attempts that have not yet succeeded are written to `UndeliveredFolder` before the process exits.

Example ASP.NET Core hosted service integration:

```csharp
public sealed class MailQueueShutdownFlushService : IHostedService
{
    private readonly MailGrpcServiceClientWithRetry mailClient;

    public MailQueueShutdownFlushService(MailGrpcServiceClientWithRetry mailClient)
    {
        this.mailClient = mailClient;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return this.mailClient.StopAsync(
            persistInFlight: true,
            cancellationToken: cancellationToken);
    }
}
```

Expected semantics:

- Graceful shutdown can persist in-flight retries that have not completed successfully.
- Successfully queued messages are not written to `UndeliveredFolder` by the shutdown flush.
- Messages already persisted by a failed queue attempt are not written a second time by the same in-flight operation.
- `StopAsync(...)` stops the background resend timer and rejects new queue attempts for that client instance.
- A hard process kill, power loss, or container termination that does not allow graceful shutdown cannot guarantee in-flight persistence.

### Operational guidance for long outages

- Put `UndeliveredFolder` on **durable storage**.
- In multi-instance deployments, use a **shared folder** so one node can drain the backlog when connectivity returns.
- The temporary write file is created in the same folder as the final `*.mail` file so publication stays on one filesystem mount.
- Do not treat `*.mail.writing-*.tmp` files as pending mail in health checks or operations dashboards. Report only final `*.mail` files as resendable backlog.
- Health checks may read `.resend.lock` metadata to report owner, active or released state, lock age, expiry, and access failures. If the file cannot be read while another process owns it, report the lock state as unknown/active rather than deleting or modifying it.
- The resend loop is designed for **at-least-once** behaviour. In rare cases (for example, a network failure after the server accepted the request but before the client received the reply) a resend can create duplicates.
  - If you need strong deduplication, introduce an application-level message id (for example in a custom header) and have your downstream process treat it as an idempotency key.
- Logs and health checks should use file metadata only. Do not log retry message content, recipient addresses, or other mail payload data.

### Shared-folder verification checklist

After publishing a hardened `MailQueueNet.Common` package and before updating consuming applications, verify the target shared retry volume:

1. Start two clients against the same `UndeliveredFolder` and force both resend loops to start at the same time.
2. Confirm exactly one client processes the current final `*.mail` backlog.
3. Confirm the second client observes an active `.resend.lock`, skips resend, and leaves final `*.mail` files untouched.
4. Kill or terminate the lock owner during resend and confirm another client can recover after the OS/shared filesystem releases the handle or after the configured timeout.
5. Confirm retry health reports lock existence, owner, active/stale or unknown state, lock age, expiry, and access failures using metadata only.
6. Repeat the verification on the Linux-based Docker volume used by the deployment, for example `/var/lib/flockwork/mail-retry`.

## Configuration via `appsettings.json` (NET 9)

If your client uses the `MailClientConfiguration` static initialiser (NET 9+), you can bind via configuration under the section:

- `MailClientConfigurationSettings`

Example:

```json
{
  "MailClientConfigurationSettings": {
    "MailQueueNetServiceChannelAddress": "https://mailqueue.net",
    "EnableDiskResilience": true,
    "UndeliveredFolder": "C:\\mailqueue\\undelivered",
    "RetryCount": 5,
    "RetryBackoffFactor": 2.0,
    "UnsentCheckIntervalMinutes": 10,
    "ResendWindowHours": 72,
    "DistributedLockTimeoutSeconds": 300,
    "LockFileName": ".resend.lock",
    "AlertEmailAddress": "ops@example.com",
    "SmtpHost": "smtp.example.com",
    "SmtpPort": 587,
    "SmtpEnableSsl": true,
    "SmtpUsername": "smtp-user",
    "SmtpPassword": "smtp-password"
  }
}
```

