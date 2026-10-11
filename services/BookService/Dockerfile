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
# Run as the non-root user that the image provides. The app listens on port 8080.
USER $APP_UID
ENTRYPOINT ["dotnet", "BookService.dll"]
