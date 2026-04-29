---
slug: /mailqueuenet
title: MailQueueNet Stack
sidebar_position: 1
---

## What is MailQueueNet?

MailQueueNet is a small stack of .NET services and libraries that work together to reliably queue, process, and send emails.

The stack is designed to:

- **Decouple** application code from SMTP/third-party mail providers.
- **Improve reliability** through queueing, retries, and operational visibility.
- **Support mail merge workflows** (template + data batches) as a first-class use case.
- **Provide operator tooling** for diagnostics, administration, and troubleshooting.

## High-level architecture

At a high level:

1. Client applications queue mail (single, bulk, or merge batches) using shared client libraries.
2. The queue service accepts requests (gRPC) and persists queued work to durable storage.
3. Background workers process queued work and deliver via configured providers.
4. Operator UIs and admin APIs provide visibility and control (pause/resume, retry, delete, inspect attachments, etc.).

## The players (projects)

### `MailQueueNet.Common`

A shared library containing:

- gRPC contracts (generated from `.proto` files)
- Shared models and helpers used by both clients and services

This project is the â€œcontract + shared primitivesâ€ layer used across the stack.

For details on using this project from client applications (including retries and offline resilience), see:

- [`MailQueueNet.Common` for clients](./common.md)

### `MailQueueNet.Core`

Core abstractions and mail-sending building blocks used by the service implementation.

Typical contents include provider implementations and shared "core" logic that should not depend on the web host.

See:

- [`MailQueueNet.Core`](./core.md)

### `MailQueueNet.Service`

The queueing and processing service.

Responsibilities include:

- Hosting the gRPC service
- Persisting queued work
- Running background processing loops
- Implementing operational/admin endpoints (pause/resume, retry, delete, logs, stats, etc.)

See:

- [`MailQueueNet.Service`](./service.md)

### `MailForge`

A companion service focused on **mail merge orchestration**.

This component takes merge definitions (template + JSONL data rows), expands them into individual mails, and queues the resulting mail items for delivery.

See:

- [`MailForge`](./mailforge.md)

### `MailFunk`

The Blazor-based operator UI.

This provides an operational interface for:

- Monitoring folders/queues and recent activity
- Viewing individual queued mail items
- Retrying or deleting failed/queued items
- Inspecting attachments and merge state

See:

- [`MailFunk`](./mailfunk.md)

### `Tests`

Automated tests covering core behaviour and integration points.

## Where to go next

- Start with the **Architecture** section for request flows and responsibilities.
- Use the **Operations** section for runbooks (pause/resume, retry workflows, log access).
- Use the **API Reference** (generated via DocFX) for the authoritative contract for public APIs.

