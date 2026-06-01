# ─── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore only the main project (not test projects) to keep the image lean.
COPY CandyBot.csproj ./
RUN dotnet restore CandyBot.csproj

# Copy the rest of the source and publish a self-contained Release build.
COPY . .
RUN dotnet publish CandyBot.csproj -c Release -o /app/publish /p:UseAppHost=false

# ─── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Copy published output (includes scripts/ via CopyToOutputDirectory in csproj).
COPY --from=build /app/publish .

# ASP.NET Core images since .NET 8 expose port 8080 as the default HTTP port
# and define APP_UID (typically 1654) with a matching non-root user "app".
EXPOSE 8080

# Pre-create the directory that PluginConfig writes to so the non-root user
# can create files there at runtime.  Operators should mount a host directory
# over /app/configs (and optionally /app/data for SQLite) to persist state
# across container restarts, e.g.:
#   -v /host/configs:/app/configs
#   -v /host/data:/app/data
RUN mkdir -p /app/configs && chown $APP_UID /app/configs

# Run as the non-root "app" user provided by the base image ($APP_UID ≈ 1654).
USER $APP_UID

ENTRYPOINT ["dotnet", "CandyBot.dll"]
