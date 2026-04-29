# Docker dev permissions (WSL/Linux)

When using bind mounts (e.g. `./Logs`, `./mail`, `./data`) on WSL/Linux, files created by containers can
end up owned by `root`, which then causes `Permission denied` when running locally (e.g. Visual Studio WSL
profiles) under a normal user.

This repository configures the key services to run with the host user's UID/GID:

- `mailqueueservice`
- `mailfunk`
- `mailforge`

This is done via `user: "${HOST_UID}:${HOST_GID}"` in `docker-compose.yml`.

## Setup

1. Copy `.env.example` to `.env` (next to `docker-compose.yml`).
2. Set `HOST_UID` and `HOST_GID`.

On WSL/Linux/macOS:

```sh
cp .env.example .env
printf "HOST_UID=%s\nHOST_GID=%s\n" "$(id -u)" "$(id -g)" > .env
```

## Notes

- If you already have root-owned files in the bind-mounted folders, you may need to fix ownership once:

```sh
sudo chown -R $(id -u):$(id -g) ./Logs ./mail ./data
```

- If you run Docker Desktop on Windows without WSL bind mounts, UID/GID mapping may not matter. When
  running containers against WSL-mounted paths (e.g. `/mnt/d/...`), this configuration avoids log and DB
  write failures.
