# Build from the repository root:  docker build -f src/Planner.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project files first so a source-only change does not invalidate the restore layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Planner.Domain/Planner.Domain.csproj src/Planner.Domain/
COPY src/Planner.Contracts/Planner.Contracts.csproj src/Planner.Contracts/
COPY src/Planner.Infrastructure/Planner.Infrastructure.csproj src/Planner.Infrastructure/
COPY src/Planner.Api/Planner.Api.csproj src/Planner.Api/
RUN dotnet restore src/Planner.Api/Planner.Api.csproj

COPY src/ src/
RUN dotnet publish src/Planner.Api/Planner.Api.csproj \
    -c Release \
    -o /app \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# curl is here purely so the container can answer its own health check.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# Token signing keys live on a mounted volume. Creating the directory with the right owner here means
# a fresh named volume inherits that ownership, so the non-root app user can write to it.
RUN mkdir -p /var/lib/planner/keys && chown -R app:app /var/lib/planner

USER app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=3s --start-period=40s --retries=5 \
    CMD curl --fail --silent http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Planner.Api.dll"]
