# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 5002
EXPOSE 5003


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["NuGet.config", "."]
COPY ["MailForge/MailForge.csproj", "MailForge/"]
RUN dotnet restore "./MailForge/MailForge.csproj"
COPY . .
WORKDIR "/src/MailForge"
RUN dotnet build "./MailForge.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MailForge.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final

# Create /https as root (base stage runs as non-root) then revert to APP_UID
USER root
RUN mkdir -p /https
COPY ./Certs/mailqueuenet.pfx /https/mailqueuenet.pfx
RUN chown -R $APP_UID:$APP_UID /https
USER $APP_UID

WORKDIR /app

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MailForge.dll"]