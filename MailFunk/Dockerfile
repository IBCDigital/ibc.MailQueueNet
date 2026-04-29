# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Build args for dev cert and admin client cert
ARG CERT_PASSWORD=DevPassword123!
ARG ADMIN_CERT_PASSWORD=5752B93EAC30EEAC8B84FE

# Expose password values as env (runtime will inherit unless overridden)
ENV CERT_PASSWORD=${CERT_PASSWORD}
ENV ADMIN_CERT_PASSWORD=${ADMIN_CERT_PASSWORD}

# Internal feeds
COPY ./NuGet.config ./NuGet.config

# Project files for restore
COPY ./MailFunk/*.csproj ./MailFunk/
COPY ./MailQueueNet.Common/*.csproj ./MailQueueNet.Common/

RUN dotnet restore ./MailFunk/MailFunk.csproj

# Copy full source (includes Certs folder) and publish
COPY . .
# Copy the admin access certificate into a known build-stage path
# Ensure the file exists at Certs/AdminAccess.pfx relative to build context.
COPY ./Certs/AdminAccess.pfx /tmp/admin-client.pfx

RUN dotnet publish ./MailFunk/MailFunk.csproj -c Release -o /app/publish /p:UseAppHost=false /t:Rebuild

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Build args must be re-declared per-stage; needed so we can persist the value into runtime ENV.
ARG ADMIN_CERT_PASSWORD=5752B93EAC30EEAC8B84FE

# Install diagnostic tools (wget, curl, ping)
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget iproute2 procps net-tools curl iputils-ping \
 && rm -rf /var/lib/apt/lists/*

# Certificates directory; mailqueuenet.pfx and admin-client.pfx copied in
RUN mkdir -p /https
COPY ./Certs/mailqueuenet.pfx /https/mailqueuenet.pfx
COPY --from=build /tmp/admin-client.pfx /https/admin-client.pfx
COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "MailFunk.dll"]