# MailQueueNet staging stack mirror

This folder mirrors the staging deployment location:

- `/mailqueuenet-stack`

## Persistent data layout

All persistent data is stored under `app/`. Each container has a dedicated subfolder containing:

- `data/` — bind-mounted into the container at `/data`
- `.env` — container environment values (not committed)

Helper scripts live in:

- `app/scripts/`

## Getting started (staging)

1. Copy each `app/*/.env.example` to `app/*/.env` and set the appropriate secrets/addresses.
2. Copy `app/scripts/.env.example` to `app/scripts/.env` and set the staging server and registry credentials as needed.
2. Build and push the images to the internal registry:

   - `./app/scripts/push-images-to-registry.ps1`

2. Sync the stack folder to the staging server.
3. On the server, run `docker compose up -d` from `/wwwroot/wwwdocs/mailqueuenet-stack`.

## Microsoft SSO (Entra ID) for MailFunk

MailFunk supports operator sign-in via **Microsoft Entra ID (Azure AD) OpenID Connect**.

For staging, the intended public hostname is:

- `https://mailfunk.dev.ibc.com.au`

### Entra application setup (staging)

1. In the Entra admin centre, add an application using:
   - **Register an application to integrate with Microsoft Entra ID (App you're developing)**.
2. Configure **Authentication**:
   - Platform: **Web**.
   - Redirect URI: `https://mailfunk.dev.ibc.com.au/signin-oidc`.
3. Create a client secret:
   - App registration → **Certificates & secrets** → **New client secret**.
4. Configure API permissions:
   - Microsoft Graph (Delegated): `User.Read`.
5. (Recommended) Restrict access via the corresponding **Enterprise application**:
   - Require assignment.
   - Assign the operator group(s).

### Staging configuration

Set these values in `app/mailfunk/.env` (do not commit secrets):

- `AzureAd__TenantId`
- `AzureAd__ClientId`
- `AzureAd__ClientSecret`

MailFunk sign-in endpoints:

- `/signin`
- `/signout`

## Nginx Proxy Manager (NPM) integration

Staging environments commonly use Nginx Proxy Manager (NPM) to terminate TLS and proxy requests to containers.

To allow NPM to reach the stack containers by name, attach the services that need to be proxied to the NPM Docker network:

- `open-appsec-ningx-proxy-manager-webgui_default`

In this stack, the following services are attached to the NPM network:

- `mailfunk`
- `mailqueuenet-service`

Operational notes:

- When TLS is terminated at the proxy, configure services to listen on HTTP internally (for example `ASPNETCORE_URLS=http://0.0.0.0:<port>`).
- gRPC requires HTTP/2. When proxying gRPC to an internal HTTP endpoint (h2c), ensure the upstream service is configured to accept HTTP/2 on that port.
- If a service is configured to bind HTTPS endpoints, it must have a valid certificate configured. When using TLS termination at the proxy, prefer HTTP-only internal bindings.
- If you require **mTLS client certificates** between MailFunk and the gRPC services, do not terminate TLS upstream of the service; keep service-to-service calls on HTTPS.
