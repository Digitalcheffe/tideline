# Tideline image (docs/11-deploy.md). Build from the repository root:
#   docker build -f deploy/Dockerfile -t tideline:local .
# Pass --build-arg RUN_TESTS=0 to skip the .NET tests inside the build (they take a couple of minutes).

# 1. the web app
FROM node:24.14.0-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm test && npm run build

# 2. the server and the command-line tool, tested on Linux, then published
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG RUN_TESTS=1
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props tideline.sln ./
COPY src/ src/
COPY tests/ tests/
COPY docs/ docs/
RUN dotnet restore tideline.sln
RUN dotnet build tideline.sln -c Release --no-restore
RUN if [ "$RUN_TESTS" = "1" ]; then dotnet test tideline.sln -c Release --no-build; fi
RUN dotnet publish src/Tideline.Api -c Release --no-build -o /out/api \
 && dotnet publish src/Tideline.Cli -c Release --no-build -o /out/cli

# 3. what runs
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS run
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin tideline \
 && mkdir -p /data /backups /inbox \
 && chown tideline:tideline /data /backups /inbox
WORKDIR /app
COPY --from=build /out/api/ ./
COPY --from=build /out/cli/ ./cli/
COPY --from=web /src/web/dist/ ./wwwroot/
# `tideline ...` works inside the container: docker exec tideline_app tideline migrate
RUN printf '#!/bin/sh\nexec dotnet /app/cli/tideline.dll "$@"\n' > /usr/local/bin/tideline && chmod +x /usr/local/bin/tideline

ENV TIDELINE_URL=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Database__ConnectionString="Data Source=/data/db/tideline.db" \
    Tideline__DataProtectionKeys=/data/keys \
    Tideline__BackupPath=/backups
USER tideline
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Tideline.Api.dll"]
