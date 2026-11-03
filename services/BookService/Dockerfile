# Stage 1: build with the full SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, so this layer is cached until the .csproj changes
COPY BookService.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

# Stage 2: run on the smaller ASP.NET runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# curl is only used by the healthcheck in docker-compose.yml to call /health.
# The runtime image does not include it. This runs as root, before switching user.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
# Run as the non-root user that the image provides. The app listens on port 8080.
USER $APP_UID
ENTRYPOINT ["dotnet", "BookService.dll"]
