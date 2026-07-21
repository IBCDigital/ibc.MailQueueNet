This fork extends MailQueueNet and retains its original MIT licence.

# MailQueueNet

MailQueueNet is a .NET 9 mail queueing and mail merge stack. It queues .NET `MailMessage` objects for background delivery, serialises queued work for resilience, and includes supporting services for merge processing and operator administration.

## Projects

- `MailQueueNet.Service` — gRPC queue service for normal mail, bulk mail, attachments, and mail merge coordination.
- `MailForge` — mail merge worker that renders merge templates and queues generated recipient messages.
- `MailFunk` — Blazor operator/admin UI.
- `MailQueueNet.Common` — shared gRPC contracts and .NET client helpers.
- `MailQueueNet.Core` — core sender abstractions and delivery implementations.

## Local build

```powershell
dotnet build .\MailQueueNet.sln -c Release
```

Dockerfiles are provided for the deployed services:

- `MailQueueNet.Service/Dockerfile`
- `MailForge/Dockerfile`
- `MailFunk/Dockerfile`

## Deployment folders

Deployment templates and helper scripts live under `deploy/`:

- Staging: `deploy/staging/mailqueuenet-stack/`
- Production: `deploy/production/mailqueuenet-stack/`

Each deployment folder contains its own README with environment-specific instructions:

- `deploy/staging/mailqueuenet-stack/README.md`
- `deploy/production/mailqueuenet-stack/README.md`

Persistent app data and per-service environment files are under each deployment stack's `app/` folder. The deployment layout mirrors the server path:

```text
/wwwroot/wwwdocs/mailqueuenet-stack
```

## Environment files

Environment files are intentionally not committed with secrets. Copy the matching `.env.example` files to `.env` and fill in the deployment-specific values.

Staging examples:

- `deploy/staging/mailqueuenet-stack/app/mailqueuenet-service/.env.example`
- `deploy/staging/mailqueuenet-stack/app/mailforge/.env.example`
- `deploy/staging/mailqueuenet-stack/app/mailfunk/.env.example`
- `deploy/staging/mailqueuenet-stack/app/scripts/.env.example`

Production examples:

- `deploy/production/mailqueuenet-stack/app/mailqueuenet-service/.env.example`
- `deploy/production/mailqueuenet-stack/app/mailforge/.env.example`
- `deploy/production/mailqueuenet-stack/app/mailfunk/.env.example`
- `deploy/production/mailqueuenet-stack/app/scripts/.env.example`

The script `.env` file controls deployment helper defaults such as server name, remote path, Docker registry, tag, and registry credentials.

## Docker image publishing

The image push scripts build and push the stack images to the configured registry:

- `mailqueuenet-service`
- `mailforge`
- `mailfunk`

Staging:

```powershell
.\deploy\staging\mailqueuenet-stack\app\scripts\push-images-to-registry.ps1
```

Production:

```powershell
.\deploy\production\mailqueuenet-stack\app\scripts\push-images-to-registry.ps1
```

Defaults are read from the relevant `app/scripts/.env`. The default internal registry is:

```text
docker-hub.internal.ibc.com.au
```

To build and push only the MailForge image manually:

```powershell
docker build --progress=plain --file .\MailForge\Dockerfile --tag docker-hub.internal.ibc.com.au/mailforge:latest .
docker push docker-hub.internal.ibc.com.au/mailforge:latest
```

## Updating a deployed MailForge container

After pushing a new `mailforge` image, update the target server from the deployed stack folder:

```sh
cd /wwwroot/wwwdocs/mailqueuenet-stack
docker compose pull mailforge
docker compose up -d --no-deps mailforge
docker logs --tail 100 mailforge
```

Alternatively, use the deployment helper scripts:

- Staging: `deploy/staging/mailqueuenet-stack/app/scripts/compose-up.ps1`
- Production: `deploy/production/mailqueuenet-stack/app/scripts/compose-up.ps1`

## Usage as a queuing service

Install/reference `MailQueueNet.Common` in your client project and create a gRPC client:

```csharp
var mailChannel = GrpcChannel.ForAddress("https://localhost:5001");
var mailClient = new MailQueueNet.Grpc.MailGrpcService.MailGrpcServiceClient(mailChannel);
```

Use the generated client and common helper APIs to add mail to the queue, send bulk mail, manage attachments, or queue mail merge templates.

## Usage as a library

You can directly reference `MailQueueNet.Core` and use `SenderFactory` to send mail without the queue service.

## License

All the code here is under MIT license. Which means you could do virtually anything with the code.
I will appreciate it very much if you keep an attribution where appropriate.

    The MIT License (MIT)
    
    Copyright (c) 2013 Daniel Cohen Gindi (danielgindi@gmail.com)
    
    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:
    
    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.
    
    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
