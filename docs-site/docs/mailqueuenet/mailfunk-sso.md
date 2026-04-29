---
title: MailFunk Microsoft SSO (Entra ID)
sidebar_position: 8
---

MailFunk supports operator sign-in via **Microsoft Entra ID (Azure AD) OpenID Connect**.

This page documents how to configure Single Sign-On (SSO) for a new environment (for example staging).

## How MailFunk authenticates

MailFunk uses a “smart” authentication scheme:

- **Windows Integrated Authentication (Negotiate)** when available.
- **OpenID Connect (Entra ID / Azure AD)** as the interactive fallback.

In reverse-proxied environments (for example Nginx Proxy Manager), the OpenID Connect flow is typically used.

## Entra setup (OIDC)

### 1) Create the application

In the Entra admin centre:

1. Go to **Microsoft Entra ID** → **Enterprise applications**.
2. Select **New application**.
3. Choose:
   - **Register an application to integrate with Microsoft Entra ID (App you're developing)**.

This creates an **App registration** and a corresponding **Enterprise application** (service principal).

### 2) Configure redirect URIs

In **App registrations** → *Your app* → **Authentication**:

- Platform: **Web**.
- Redirect URI:
  - `https://<your-mailfunk-hostname>/signin-oidc`

Example (staging):

- `https://mailfunk.dev.ibc.com.au/signin-oidc`

If you implement sign-out callbacks:

- `https://<your-mailfunk-hostname>/signout-callback-oidc`

## 3) Create a client secret

In **App registrations** → *Your app* → **Certificates & secrets**:

1. Create a new **Client secret**.
2. Copy the **Value** immediately.

The secret value cannot be retrieved later; if it is lost, create a new secret and update the environment configuration.

## 4) API permissions

MailFunk requests Microsoft Graph delegated permissions so it can read the signed-in user profile.

In **App registrations** → *Your app* → **API permissions**:

- Microsoft Graph (Delegated): `User.Read`

If your tenant requires admin consent, grant it.

## Enterprise application hardening (recommended)

In **Enterprise applications** → *Your app*:

- Require user assignment.
- Assign access to the appropriate operator group(s).
- Apply Conditional Access policies as required.

## Application configuration (MailFunk)

MailFunk reads the following configuration keys:

- `AzureAd:TenantId`
- `AzureAd:ClientId`
- `AzureAd:ClientSecret`

In container deployments, these are typically provided via environment variables:

- `AzureAd__TenantId`
- `AzureAd__ClientId`
- `AzureAd__ClientSecret`

MailFunk sign-in and sign-out endpoints:

- `/signin`
- `/signout`

## Troubleshooting

### Redirect URI mismatch

If sign-in fails with an Entra error about redirect URIs:

- Confirm the application redirect URI exactly matches the public hostname served by your reverse proxy.
- Confirm the path is `/signin-oidc`.

### Reverse proxy TLS termination

When running behind a reverse proxy that terminates TLS:

- Ensure users access MailFunk via `https://`.
- If the application behaves as if it is on `http://` (for example incorrect redirects), configure forwarded headers handling in the host.
