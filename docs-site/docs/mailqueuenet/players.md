---
title: Players (Projects)
sidebar_position: 2
---

This page describes the responsibilities and boundaries of each project in the MailQueueNet stack.

## `MailQueueNet.Common`

**Role:** shared contracts and shared types.

Use this when you need:

- gRPC client/server contract types (generated from `.proto`)
- Shared DTOs and helper utilities that must be consistent across all components

**Notes:** because this project is referenced broadly, keep dependencies minimal and avoid adding service-hosting concerns.

For client-focused usage patterns (including retries, disk resilience, and attachment handling), see:

- [`MailQueueNet.Common` for clients](./common.md)

## `MailQueueNet.Core`

**Role:** core mail delivery abstractions and provider implementations.

Use this when you need:

- Sender/provider implementations (e.g. SMTP, third-party providers)
- Internal building blocks that the service uses to deliver messages

**Notes:** this should be the layer where â€œsending mailâ€ logic lives, separate from hosting concerns.

For more details, see:

- [`MailQueueNet.Core`](./core.md)

## `MailQueueNet.Service`

**Role:** the running queue service (gRPC host + background processing).

Responsibilities typically include:

- Accepting queue requests over gRPC
- Writing queued work to disk/database
- Managing retries and success/failure handling
- Serving operational endpoints (logs, stats, pause/resume, attachment administration)

**Operational focus:** this is the component operators deploy and monitor.

For service behaviour, configuration, and storage requirements, see:

- [`MailQueueNet.Service`](./service.md)

## `MailForge`

**Role:** mail merge coordinator and batch processor.

Use this when you need:

- Template-based mail generation
- Batch workflows (JSONL row input, batch acknowledgement)

**Notes:** keeps merge concerns separate from the core queue service.

For mail merge orchestration, batch storage, and resilience details, see:

- [`MailForge`](./mailforge.md)

## `MailFunk`

**Role:** Blazor operator UI.

Use this when you need:

- A UI for operators/admins to inspect and manage queued mail
- Diagnostics and workflows (retry/delete, attachment inspection, merge monitoring)

**Notes:** this should remain a â€œconsumerâ€ of service APIs; keep business logic in service libraries where possible.

For operator setup, security, and feature walkthrough, see:

- [`MailFunk`](./mailfunk.md)

## `Tests`

**Role:** automated checks and regression coverage.

Use this when you need:

- Integration-style tests that validate gRPC and persistence behaviour
- Unit tests for core utilities and workflows

## Suggested ownership boundaries

- Treat `.proto` and the generated gRPC types as **public API surface area**.
- Prefer putting shared contracts in `MailQueueNet.Common`, not in UI/service projects.
- Keep UI concerns in `MailFunk`; avoid leaking UI-specific types into shared libraries.

