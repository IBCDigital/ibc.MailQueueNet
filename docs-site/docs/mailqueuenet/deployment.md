---
title: Deployment and Staging
sidebar_position: 9
---

This page describes the expected deployment shape for the MailQueueNet stack, with emphasis on container deployments and the staging mirror used by this repository.

## Deployment components

A full stack deployment normally includes:

- `MailQueueNet.Service` — queue service and mail delivery host.
- `MailForge` — mail merge worker.
- `MailFunk` — Blazor Server operator UI.
- Mailpit or another SMTP capture service for staging and development.
- Persistent storage for queue, failed, attachments, merge state, logs, and worker state.

## Staging server location

The staging Docker server is expected to be:

- Hostname: `docker-dev-internal.ibc.com.au`
- Address: `192.168.20.128`
- Stack folder: `/wwwroot/wwwdocs/mailqueuenet-stack`

The repository contains a local mirror of that folder under:

```text
deploy/staging/wwwroot/wwwdocs/mailqueuenet-stack
```

Use the mirror when preparing compose, environment example, and helper script updates before syncing to the server.

## Folder layout

All persistent folders should live under `app/`. Each container has its own subfolder, and helper scripts live under `app/scripts`.

```text
mailqueuenet-stack/
├─ docker-compose.yml
├─ README.md
└─ app/
   ├─ mailqueuenet-service/
   │  ├─ .env
   │  └─ data/
   ├─ mailforge/
   │  ├─ .env
   │  └─ data/
   ├─ mailfunk/
   │  ├─ .env
   │  └─ data/
   └─ scripts/
```

Real `.env` files contain secrets and must not be committed. Use the committed `.env.example` files as templates.

## Persistent data map

Recommended bind mounts:

| Container | Host path | Container path | Purpose |
| --- | --- | --- | --- |
| `mailqueuenet-service` | `./app/mailqueuenet-service/data` | `/data` | Queue folders, failed folders, merge queue, attachments, SQLite indexes, logs |
| `mailforge` | `./app/mailforge/data` | `/data` | Job state, batch rows, fence database, logs, undelivered queue-back messages |
| `mailfunk` | `./app/mailfunk/data` | `/data` | MailFunk logs and other UI-local state |

Do not rely on the container writable layer for state that must survive container recreation.

## Required environment values

Each container has its own `.env` file.

### `app/mailqueuenet-service/.env`

Important values:

- `ASPNETCORE_ENVIRONMENT=Staging`
- Kestrel URL and certificate settings.
- `Security__SharedClientSecret`
- Queue folders under `/data/mail/...`
- `Attachments__Path=/data/attachments`
- `Attachments__IndexDbPath=/data/db/attachment_index.db`
- `mailforge__dispatcher__stateDbPath=/data/db/dispatcher_state.db`
- `mailforge__dispatcher__workerAddresses__0=https://mailforge:5003`
- Staging mail routing settings, including Mailpit and real SMTP settings.

### `app/mailforge/.env`

Important values:

- `ASPNETCORE_ENVIRONMENT=Staging`
- Kestrel URL and certificate settings.
- `MailQueue__Address=https://mailqueuenet-service:5001`
- `MailQueue__ClientId`
- `MailQueue__SharedSecret`
- `MailForge__JobWorkRoot=/data/work`
- `MailForge__FenceDbPath=/data/db/dispatch_fence.db`
- `MAILFORGE_BATCH_FOLDER=/data/batches`
- `FileLogging__Path=/data/logs/mailforge`

### `app/mailfunk/.env`

Important values:

- `ASPNETCORE_ENVIRONMENT=Staging`
- Kestrel URL and certificate settings.
- `MailService__BaseAddress=https://mailqueuenet-service:5001`
- `MailForge__BaseAddress=https://mailforge:5003`
- `AdminClientCert__Path=/https/admin-client.pfx`
- `AdminClientCert__Password`
- Azure AD settings for operator sign-in.
- `FileLogging__Path=/data/logs/mailfunk`

## Compose validation

Before applying staging changes, validate compose syntax on a machine with Docker available:

```powershell
cd deploy/staging/wwwroot/wwwdocs/mailqueuenet-stack
docker compose config
```

On the staging server, run from `/wwwroot/wwwdocs/mailqueuenet-stack`:

```bash
docker compose up -d --remove-orphans
```

## Sync workflow

Helper scripts are stored in `app/scripts`.

Common flow from the repository root mirror:

```powershell
cd deploy/staging/wwwroot/wwwdocs/mailqueuenet-stack
./app/scripts/sync-to-staging.ps1 -DryRun
./app/scripts/sync-to-staging.ps1
./app/scripts/compose-up.ps1
```

Use `-IncludeEnv` only when you intentionally want to copy local `.env` files. Treat those files as secrets.

## Staging smoke tests

After deployment, verify:

1. `docker compose ps` shows all expected services healthy or running.
2. `MailQueueNet.Service` accepts a basic queue request.
3. Mailpit receives captured mail.
4. A non-allow-listed recipient does not receive a real SMTP copy.
5. An allow-listed recipient receives the real SMTP copy with the staging subject marker.
6. MailForge can process a small merge batch and queue rendered messages back to the service.
7. MailFunk can sign in, load the dashboard, read logs, and perform an admin-only read action.
8. Persistent folders under `app/<container>/data` contain expected logs and state after restart.

## Rollback notes

For a simple rollback:

1. Keep the previous compose file and image tags available.
2. Stop the stack with `docker compose down` only if required.
3. Restore the previous compose/image tags.
4. Run `docker compose up -d --remove-orphans`.
5. Do not delete `app/<container>/data` unless you are deliberately clearing persistent queue and worker state.
