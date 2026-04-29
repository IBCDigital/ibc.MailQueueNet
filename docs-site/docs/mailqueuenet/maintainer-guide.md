---
title: Maintainer Guide
sidebar_position: 8
---

This guide is for developers maintaining the MailQueueNet stack itself. It complements the client and operator documentation by explaining how to make safe code, contract, configuration, and deployment changes.

## Local development setup

Required tools:

- Visual Studio 2026 or later, or the .NET 9 SDK with a compatible editor.
- Docker Desktop when validating the full container stack.
- Node.js 20 or later for the Docusaurus documentation site.
- A local certificate set under `Certs/` when running HTTPS and mTLS-enabled workflows.

Useful entry points:

| Area | Project or folder |
| --- | --- |
| Queue service | `MailQueueNet.Service` |
| Shared client contract | `MailQueueNet.Common` |
| Delivery providers | `MailQueueNet.Core` |
| Mail merge worker | `MailForge` |
| Blazor operator UI | `MailFunk` |
| Automated tests | `Tests` |
| Authored documentation | `docs-site/docs` |
| API documentation configuration | `docfx` |
| Staging deployment mirror | `deploy/staging/wwwroot/wwwdocs/mailqueuenet-stack` |

If you want Visual Studio to show deployment folders, scripts, and docs alongside the projects, open the repository with **File → Open → Folder...** instead of opening only the `.sln` file.

## Documentation scripts

The `scripts` folder contains the supported documentation workflow:

| Script | Purpose |
| --- | --- |
| `scripts/build-api-docs.ps1` | Restores .NET tools and builds DocFX API output into `docs-site/static/api`. |
| `scripts/build-docs.ps1` | Builds DocFX output, installs Docusaurus dependencies, and builds the static Docusaurus site. |
| `scripts/start-docs-dev.ps1` | Builds the complete site into a local preview root and serves it at `http://localhost:3000/MailQueueNet/`. |
| `scripts/refresh-docfx-preview.ps1` | Rebuilds only DocFX output and copies it into an already running preview root. |
| `scripts/publish-docusaurus.ps1` | Builds and publishes the static site to the internal documentation server using SSH/SCP. |

Use `scripts/build-docs.ps1` before publishing documentation changes.

## Architecture boundaries

Keep these boundaries clear:

- `MailQueueNet.Common` is the public client contract. Avoid host-specific dependencies and treat `.proto` messages as versioned public API.
- `MailQueueNet.Core` owns delivery provider behaviour such as SMTP and Mailgun integration.
- `MailQueueNet.Service` owns queue persistence, gRPC hosting, security checks, staging routing, background workers, and operational endpoints.
- `MailForge` owns mail merge rendering and merge job state. It queues rendered mail back through `MailQueueNet.Service`.
- `MailFunk` is an operator UI and should consume service APIs rather than duplicating server-side business rules.

## Public contract change checklist

A change is a public contract change when it modifies `.proto` files, `MailQueueNet.Common` helpers, configuration keys, gRPC authentication requirements, attachment semantics, or operator workflows.

When changing the contract:

1. Prefer additive changes over renaming or reusing fields.
2. Do not reuse protobuf field numbers.
3. Keep existing request and reply fields compatible unless a coordinated client upgrade is planned.
4. Regenerate and compile the generated gRPC code through a normal build.
5. Add or update helper methods in `MailQueueNet.Common` when the raw generated client is awkward for application developers.
6. Add tests for the helper or server behaviour.
7. Update `docs-site/docs` and API XML comments in the same change.

## Configuration and secrets

Configuration is supplied through appsettings and environment variables. In containers, environment variables are preferred for deployment-specific values and secrets.

Important areas:

| Area | Common keys |
| --- | --- |
| Client auth | `Security:SharedClientSecret` |
| Admin shared-secret auth | `Security:AdminSharedSecret` |
| Admin mTLS | `Security:AdminCertThumbprints` |
| Queue storage | `queue:queue_folder`, `queue:failed_folder`, `queue:mail_merge_queue_folder` |
| Attachments | `Attachments:Path`, `Attachments:IndexDbPath` |
| Dispatcher state | `mailforge:dispatcher:stateDbPath` |
| MailForge worker state | `MailForge:JobWorkRoot`, `MailForge:FenceDbPath`, `MAILFORGE_BATCH_FOLDER` |
| MailFunk service targets | `MailService:BaseAddress`, `MailForge:BaseAddress` |

Do not commit real `.env` files, shared secrets, certificate passwords, Azure AD client secrets, SMTP passwords, or private certificates.

## Persistent storage expectations

The stack relies on durable storage for reliability. In container deployments, bind-mount or volume-mount these paths rather than using the container filesystem:

- MailQueueNet queue, failed, and mail merge folders.
- MailQueueNet attachment store and attachment index database.
- MailQueueNet dispatcher state database.
- MailForge job work root and fence database.
- MailForge batch folder.
- Client-side undelivered folders when disk resilience is enabled.
- File logs when logs must survive container recreation.

The staging deployment mirror stores persistent folders under `app/<container>/data` and helper scripts under `app/scripts`.

## Security model summary

MailQueueNet uses three authentication patterns:

| Caller type | Mechanism | Typical use |
| --- | --- | --- |
| Client application | `x-client-id` + `x-client-pass` derived from `Security:SharedClientSecret` | Queue mail, upload attachments, manage own staging allow-list |
| Operator/admin UI | mTLS client certificate thumbprint allow-list | MailFunk admin workflows |
| Automation/admin script | `x-admin-id` + `x-admin-pass` plus replay headers | Admin mutation workflows where mTLS is not practical |

Admin mutation calls require replay protection headers (`x-ts` and `x-nonce`) when using shared-secret admin authentication.

Staging allow-list endpoints are intentionally client-authenticated for normal clients. Admin callers can manage another client's list by supplying a target client id.

## Staging mail routing maintenance notes

Staging routing is a safety feature, not a production mail-routing feature.

Expected behaviour in `Staging`:

- Mailpit receives the captured message by default.
- Real SMTP delivery is only sent to allow-listed recipients for the authenticated client id.
- Real SMTP deliveries include a subject marker.
- Non-allow-listed recipients are removed from the real SMTP copy.

When changing this area, test all recipient fields (`To`, `Cc`, and `Bcc`) and include cases where no recipients are allow-listed.

## Testing and validation

Before finishing a change:

1. Build the solution.
2. Run targeted tests for the changed area.
3. Run broader tests when public contracts, queue persistence, authentication, attachment handling, or staging routing are changed.
4. Build the documentation site when authored docs change:

```powershell
.\scripts\build-docs.ps1
```

Useful targeted areas:

| Change area | Validation focus |
| --- | --- |
| `MailQueueNet.Common` helpers | Client helper unit tests and generated gRPC compile |
| `.proto` files | Full build and client/server call tests |
| Queue processing | Service tests and manual queue/failure-folder checks |
| Attachment handling | Upload, queue, download, cleanup, and token reference counts |
| MailForge | Merge dispatch, render, queue-back, acknowledgement, and resume after restart |
| MailFunk | Blazor build plus UI smoke test against local services |
| Deployment files | `docker compose config` and staging mirror dry-run scripts |

## Documentation maintenance

Update documentation when changing:

- Public APIs or gRPC contracts.
- Configuration keys or environment variables.
- Deployment layout or persistent storage requirements.
- Security and authentication requirements.
- Operator workflows in MailFunk.
- Background jobs, queue semantics, attachment semantics, or staging routing.

Prefer updating existing docs over creating duplicate pages. Use Australian English in comments, XML docs, and user-facing strings.

## Troubleshooting pointers for maintainers

Start with these checks:

| Symptom | First checks |
| --- | --- |
| Client cannot queue mail | Service address, TLS trust, `x-client-id`, `x-client-pass`, and `Security:SharedClientSecret` |
| Attachments missing remotely | Whether the client used token upload helpers and whether `Attachments:Path` is durable/writable |
| MailForge job stuck | Worker address, fence database, job work root, MailQueue credentials, and batch files |
| MailFunk admin actions fail | Admin certificate path/password/thumbprint and service-side `Security:AdminCertThumbprints` |
| Staging real delivery missing | Environment is `Staging`, allow-list client id matches, recipient is allow-listed, `ForceMailpitOnly` is false |
| Queue not draining | Pause status, worker limit, queue folder permissions, failed folder growth, provider settings |

## Release and deployment checklist

For a release or staging deployment:

1. Confirm version numbers and package versions for any `MailQueueNet.Common` change.
2. Build and test the solution.
3. Build the docs site if docs changed.
4. Build container images with the expected staging tags.
5. Check the staging compose file and `.env.example` values for new required settings.
6. Sync the local staging mirror to `/wwwroot/wwwdocs/mailqueuenet-stack`.
7. Run `docker compose config` and then `docker compose up -d --remove-orphans` on the staging server.
8. Smoke test MailQueueNet.Service, MailForge, MailFunk, Mailpit capture, and staging allow-list real SMTP delivery.
