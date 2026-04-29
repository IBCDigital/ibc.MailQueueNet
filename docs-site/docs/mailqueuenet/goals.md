---
title: Goals and non-goals
sidebar_position: 10
---

## Goals

MailQueueNet aims to provide a predictable, operationally friendly email delivery pipeline.

Key goals:

1. **Reliability**
   - Queue mail requests durably.
   - Retry failures with clear reporting.

2. **Separation of concerns**
   - Client applications submit mail without needing to embed provider logic.
   - Delivery and provider integration live in the service/core layers.

3. **Operational visibility and control**
   - Operators can inspect queued/failed items.
   - Operators can pause/resume processing, retry, and perform bulk actions.
   - Logs and usage statistics are available for troubleshooting.

4. **First-class mail merge**
   - Support template + data batch queueing.
   - Provide clear batch lifecycle (queue → process → acknowledge completion).

5. **Consistency through contracts**
   - gRPC contracts and shared types are versioned and reused across all components.

## Non-goals

These are intentionally out of scope unless explicitly added:

- Acting as a full marketing automation platform.
- Providing rich template authoring UI (template editing is expected to occur upstream).
- Replacing provider-specific admin consoles.

## Documentation sources

- **Authored docs (this site):** operator workflows, architecture, runbooks.
- **DocFX API reference:** generated from C# XML comments.
- **In-code XML comments:** canonical API intent.
