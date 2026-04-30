# MailQueueNet production stack

This folder is the production deployment template for the MailQueueNet stack.

Recommended production deployment location:

- `/wwwroot/wwwdocs/mailqueuenet-stack`

## Persistent data layout

All persistent data is stored under `app/`. Each container has a dedicated subfolder containing:

- `data/` — bind-mounted into the container at `/data`
- `.env` — container environment values (not committed)

Helper scripts live in:

- `app/scripts/`

## Getting started (production)

1. Copy each `app/*/.env.example` to `app/*/.env` and set the appropriate secrets/addresses.
2. Copy `app/scripts/.env.example` to `app/scripts/.env` and set the production server and registry credentials as needed.
3. Build and push the images to the internal registry:

   - `./app/scripts/push-images-to-registry.ps1`

4. Sync the stack folder to the production server:

   - `./app/scripts/sync-to-production.ps1`

5. On the server, run `docker compose up -d` from `/wwwroot/wwwdocs/mailqueuenet-stack`.

## Production SSH and privilege model

Production servers commonly disable direct `root` SSH login. The helper scripts are configured for this model:

- SSH user: `ibcdigital` by default.
- Deployment path: `/wwwroot/wwwdocs/mailqueuenet-stack`.
- Upload path: `/tmp/mailqueuenet-stack-upload`.
- Privileged operations: controlled by `PRODUCTION_PRIVILEGE_MODE`.

Set these in `app/scripts/.env`:

```ini
PRODUCTION_SERVER=your-production-host
PRODUCTION_USER=ibcdigital
PRODUCTION_REMOTE_PATH=/wwwroot/wwwdocs/mailqueuenet-stack
PRODUCTION_UPLOAD_PATH=/tmp/mailqueuenet-stack-upload
PRODUCTION_PRIVILEGE_MODE=su
```

Supported privilege modes:

- `none` — no elevation. Use this when `ibcdigital` owns `/wwwroot/wwwdocs/mailqueuenet-stack` and can run Docker directly.
- `su` — scripts SSH as `ibcdigital`, upload to `/tmp`, then run `su - root -c '...'` for install and Docker Compose commands. You may be prompted for the root password by SSH.
- `sudo` — scripts run privileged commands through `sudo sh -lc '...'`. Use this only if `ibcdigital` has suitable sudo rights.
- `sudo-password` — scripts pass `PRODUCTION_PRIVILEGE_PASSWORD` to `sudo -S`. Use only if `ibcdigital` is in sudoers and you accept storing that password locally in `scripts/.env`.

The `sync-to-production.ps1` script does not copy directly into `/wwwroot`; it stages files under `/tmp` first, then installs them with the configured privilege mode.

If production disables both `su` and `sudo`, use the user-owned deployment model instead. A server administrator must run this once on the production host:

```sh
mkdir -p /wwwroot/wwwdocs/mailqueuenet-stack
chown -R ibcdigital:ibcdigital /wwwroot/wwwdocs/mailqueuenet-stack
```

Then configure:

```ini
PRODUCTION_USER=ibcdigital
PRODUCTION_REMOTE_PATH=/wwwroot/wwwdocs/mailqueuenet-stack
PRODUCTION_PRIVILEGE_MODE=none
```

Do not later `chown` the deployment folder back to `root` unless you also grant `ibcdigital` write access through a group or ACL; otherwise future syncs will not be able to overwrite files.

## Microsoft SSO (Entra ID) for MailFunk

MailFunk supports operator sign-in via **Microsoft Entra ID (Azure AD) OpenID Connect**.

Set the intended production public hostname before configuring Entra ID, for example:

- `https://mailfunk.ibc.com.au`

### Entra application setup (production)

1. In the Entra admin centre, add an application using:
   - **Register an application to integrate with Microsoft Entra ID (App you're developing)**.
2. Configure **Authentication**:
   - Platform: **Web**.
   - Redirect URI: `https://mailfunk.ibc.com.au/signin-oidc`.
3. Create a client secret:
   - App registration → **Certificates & secrets** → **New client secret**.
4. Configure API permissions:
   - Microsoft Graph (Delegated): `User.Read`.
5. (Recommended) Restrict access via the corresponding **Enterprise application**:
   - Require assignment.
   - Assign the operator group(s).

### Production configuration

Set these values in `app/mailfunk/.env` (do not commit secrets):

- `AzureAd__TenantId`
- `AzureAd__ClientId`
- `AzureAd__ClientSecret`

MailFunk sign-in endpoints:

- `/signin`
- `/signout`

## Nginx Proxy Manager (NPM) integration

Production Nginx Proxy Manager (NPM) runs on a separate host and reaches this Docker host via published HTTP ports.

Configure NPM upstreams to point at the Docker host IP and published ports:

- MailFunk UI: `http://172.1.32.126:5002`
- MailQueue gRPC: `http://172.1.32.126:5000`

The compose stack uses its own internal Docker bridge network named `mailqueuenet_stack` so the services can communicate by container DNS name:

- `mailqueuenet-service:5000`
- `mailforge:5002`
- `mailfunk:5004`

Operational notes:

- When TLS is terminated at the proxy, configure services to listen on HTTP internally (for example `ASPNETCORE_URLS=http://0.0.0.0:<port>`).
- gRPC requires HTTP/2. When proxying gRPC to an internal HTTP endpoint (h2c), ensure the upstream service is configured to accept HTTP/2 on that port.
- If a service is configured to bind HTTPS endpoints, it must have a valid certificate configured. When using TLS termination at the proxy, prefer HTTP-only internal bindings.
- If you require **mTLS client certificates** between MailFunk and the gRPC services, do not terminate TLS upstream of the service; keep service-to-service calls on HTTPS.

## Production safety notes

- Do not enable `StagingMailRouting__Enabled` in production unless intentionally testing staging behaviour.
- Set strong unique values for `Security__SharedClientSecret` and `Security__AdminSharedSecret` in every `.env` file.
- Set `queue__smtp__server`, credentials, TLS, and authentication values to the production SMTP provider before accepting real traffic.
- Keep `/data` backed up; it contains queued mail, failed mail, merge work, dispatcher state, attachment data, logs, and data-protection keys.
