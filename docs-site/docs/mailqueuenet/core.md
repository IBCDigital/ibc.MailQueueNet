---
title: MailQueueNet.Core
sidebar_position: 4
---

`MailQueueNet.Core` is the “delivery engine” layer of the stack.

It contains the abstractions and provider implementations used by `MailQueueNet.Service` to convert queued work into outbound email delivery.

## What `MailQueueNet.Core` does

In practical terms, the Core project exists to keep *delivery logic* separate from *service hosting*.

Typical responsibilities include:

- Implementing mail provider integrations (for example SMTP and third-party HTTP APIs such as Mailgun).
- Providing the core send pipeline (normalisation, timeouts, failure mapping, logging enrichment).
- Centralising cross-cutting concerns that the service depends on, without coupling them to ASP.NET hosting.

This makes it easier to:

- Unit test provider behaviour.
- Reuse senders in other hosts (for example a console runner or future worker models).
- Keep `MailQueueNet.Service` focused on queueing, persistence, and orchestration.

## How it helps the service

`MailQueueNet.Service` uses `MailQueueNet.Core` to:

- Select the configured delivery provider (`queue:mail_service_type`).
- Execute delivery attempts with consistent timeout and error behaviour.
- Produce structured logs that support operational troubleshooting.

From an operator point of view:

- Core is where “how do we talk to SMTP/Mailgun?” lives.
- Service is where “how do we queue, persist, retry, and manage mail items?” lives.

## Provider selection

The service selects a provider based on configuration (see the Service page for full details), and Core provides the implementations.

The two common provider configurations in this repository are:

- SMTP (`queue:mail_service_type = smtp`)
- Mailgun (`queue:mail_service_type = mailgun`)

If you add new providers in future, the intent is that they live in `MailQueueNet.Core` and are wired into the service’s selection logic.
