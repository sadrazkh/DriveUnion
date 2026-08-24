# syntax=docker/dockerfile:1

# Drive Union ships as one container: the .NET panel, serving its own front end.
#
# This file exists rather than letting Harbora auto-detect a .NET app, and the reason is the front
# end. Auto-detection runs `dotnet publish` and nothing else, so the Vite bundle and the Vazirmatn
# font would not exist and every page would render with no CSS and no islands. Building the assets
# locally and shipping them is not a way out either: Harbora always excludes `build`, `dist` and
# `node_modules` from the upload, and Vite's output lives in wwwroot/build. So they are built here.

# ── 1. The front end ──────────────────────────────────────────────────────────
FROM node:22-alpine AS assets
WORKDIR /web

# Manifests first, so editing a .ts file does not re-run npm ci on every build.
COPY src/DriveUnion.Web/package.json src/DriveUnion.Web/package-lock.json ./
RUN npm ci

COPY src/DriveUnion.Web/ ./

# `prebuild` copies Vazirmatn out of the pinned npm package into wwwroot/fonts. The server is in
# Germany and the design forbids a foreign CDN, so the font has to be inside the image.
RUN npm run build

# ── 2. Publish ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Directory.Build.props carries TargetFramework and Nullable for every project, so a restore that
# runs without it resolves against the wrong framework.
COPY Directory.Build.props ./
COPY src/DriveUnion.Core/DriveUnion.Core.csproj src/DriveUnion.Core/
COPY src/DriveUnion.Infrastructure/DriveUnion.Infrastructure.csproj src/DriveUnion.Infrastructure/
COPY src/DriveUnion.Web/DriveUnion.Web.csproj src/DriveUnion.Web/
RUN dotnet restore src/DriveUnion.Web/DriveUnion.Web.csproj

COPY src/ src/
COPY --from=assets /web/wwwroot/build src/DriveUnion.Web/wwwroot/build
COPY --from=assets /web/wwwroot/fonts src/DriveUnion.Web/wwwroot/fonts

RUN dotnet publish src/DriveUnion.Web/DriveUnion.Web.csproj \
      --configuration Release --no-restore --output /app

# ── 3. Runtime ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# Harbora sets PORT for auto-detected builds; a Dockerfile build has to honour it itself, and an app
# on the wrong port is the 502 in the panel's own troubleshooting table.
#
# `exec` keeps dotnet as PID 1, so a stop signal reaches it rather than the shell — without it every
# redeploy sits out the kill timeout and in-flight downloads are cut instead of drained.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet DriveUnion.Web.dll --urls http://0.0.0.0:${PORT:-8080}"]
