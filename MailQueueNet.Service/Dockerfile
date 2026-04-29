# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Allow override of dev cert password at build time (still used if needed elsewhere)
ARG CERT_PASSWORD=DevPassword123!
ENV CERT_PASSWORD=${CERT_PASSWORD}

# Copy NuGet config to enable internal feeds before restore
COPY ./NuGet.config ./NuGet.config

# Copy project files and restore only the service project
COPY ./MailQueueNet.Service/*.csproj ./MailQueueNet.Service/
COPY ./MailQueueNet.Common/*.csproj ./MailQueueNet.Common/
COPY ./MailQueueNet.Core/*.csproj ./MailQueueNet.Core/
RUN dotnet restore ./MailQueueNet.Service/MailQueueNet.Service.csproj

# Copy the rest and publish
COPY . .
RUN dotnet publish ./MailQueueNet.Service/MailQueueNet.Service.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install tools useful for diagnostics and health checks
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget iproute2 procps net-tools curl iputils-ping \
 && rm -rf /var/lib/apt/lists/*

# Create directory for certs and copy mailqueuenet.pfx from the service project's Certs folder
RUN mkdir -p /https
COPY ./Certs/mailqueuenet.pfx /https/mailqueuenet.pfx

# Copy published output
COPY --from=build /app/publish ./

# Expose HTTP (5000) and HTTPS (5001)
EXPOSE 5000 5001

# Kestrel configuration for HTTP and HTTPS; default cert path/password provided via environment (docker-compose)
ENV ASPNETCORE_URLS="http://0.0.0.0:5000;https://0.0.0.0:5001" \
 ASPNETCORE_HTTP_PORTS=5000 \
 ASPNETCORE_HTTPS_PORTS=5001 \
 DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "MailQueueNet.Service.dll"]