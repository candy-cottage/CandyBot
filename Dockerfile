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

# Give the non-root user ownership of /app so it can create runtime state there:
#   • /app/configs/   – PluginConfig files (candybot.functions, sqlconfig, etc.)
#   • /app/candybot.db – SQLite database (default DataSource "candybot" resolves to CWD)
# Only the directory entry is chowned (non-recursive); the COPY'd binaries inside
# remain root-owned and readable/executable, which is fine.
# For persistence across restarts, mount a volume covering these paths, e.g.:
#   -v /host/app:/app          (covers both configs and the SQLite DB)
# or bind the specific paths:
#   -v /host/configs:/app/configs
#   -v /host/candybot.db:/app/candybot.db
# The SQLite location and DB backend are configurable via PluginConfig (sqlconfig).
RUN chown $APP_UID /app

# Run as the non-root "app" user provided by the base image ($APP_UID ≈ 1654).
USER $APP_UID

ENTRYPOINT ["dotnet", "CandyBot.dll"]
