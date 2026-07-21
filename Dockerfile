# Mohist Server —— production image.
#
# Single container packages the ASP.NET Core + Orleans control plane together
# with the web SPA (built into wwwroot/ at publish time). All state lands under
# $HOME/.mohist/ (SQLite db, otel.db, attachments, artifacts, system-update), so
# setting HOME=/data in the runtime stage funnels every byte of state onto one
# mountable volume. See docs/self-host.md "Docker 部署".
#
# Build from repo root:
#   docker build -t mohist-server .
#
# Run (single container):
#   docker run -d -p 3456:3456 -v mohist-data:/data --name mohist mohist-server
#
# Or with the bundled compose file:
#   docker compose up -d

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — build the web SPA + publish the .NET server.
#
# The Node toolchain is an explicit compatible release. Copying it into the
# .NET builder keeps the Node version independent of the distro repository.
# ─────────────────────────────────────────────────────────────────────────────
FROM node:22.19.0-bookworm AS node-toolchain

FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS builder

COPY --from=node-toolchain /usr/local/ /usr/local/

WORKDIR /src

# The repo's global.json pins an exact preview SDK (11.0.100-preview.4...) with
# no rollForward policy, so a slightly newer SDK image tag would be rejected.
# Loosen the policy inside this build context only — written before sources are
# copied so dotnet picks any 11.0 preview SDK in the image.
RUN echo '{"sdk":{"version":"11.0.100-preview.4.26230.115","rollForward":"latestFeature"}}' > global.json

# ── Web SPA: install deps (cached), then build. ──
# Single root-level package-lock.json drives all workspaces; each workspace
# only has a package.json. Runner's package.json is copied so `npm ci` can
# resolve the workspace set, but its sources are excluded by .dockerignore and
# it is never built here (we only run `npm run build:web`).
COPY package.json package-lock.json ./
COPY packages/web/package.json packages/web/
COPY packages/runner/package.json packages/runner/

RUN --mount=type=cache,target=/root/.npm \
    npm ci

COPY packages/web/ packages/web/
RUN npm run build:web

# ── .NET server: restore, then publish (picks up the SPA above via the
#    CopyWebAssetsToPublish target which reads packages/web/dist/**). ──
# Both Directory.*.props are required: Directory.Build.props sets the TFM,
# Directory.Packages.props is the Central Package Management version hub.
COPY Directory.Build.props Directory.Packages.props ./
COPY Mohist.sln ./
COPY packages/server/ packages/server/

RUN dotnet publish packages/server/src/Mohist.Server/Mohist.Server.csproj \
      -c Release \
      -o /app \
      /p:SkipWebBuild=true \
      /p:UseAppHost=false

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — runtime.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/nightly/aspnet:11.0-preview

# curl is needed for HEALTHCHECK; the aspnet base image doesn't ship it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Non-root user (uid 1001, matching the test Containerfile convention).
RUN groupadd -g 1001 mohist && useradd -m -u 1001 -g mohist mohist

WORKDIR /app
COPY --from=builder --chown=mohist:mohist /app ./

# All Mohist stores resolve $HOME/.mohist/... at runtime, so HOME=/data makes
# every piece of state (mohist.db, otel.db, attachments, artifacts,
# system-update.json) land on the mounted volume under /data/.mohist/.
ENV HOME=/data \
    ASPNETCORE_URLS=http://0.0.0.0:3456 \
    Mohist:Host=0.0.0.0 \
    # Single-port mode: the OTLP ingestion port is off; a bind failure there is
    # already non-fatal (Program.cs falls back), but we skip it for simplicity.
    Mohist:Otel:Enabled=false

RUN mkdir -p /data && chown -R mohist:mohist /data

USER mohist
VOLUME ["/data"]
EXPOSE 3456

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -sf http://localhost:3456/api/health || exit 1

ENTRYPOINT ["dotnet", "Mohist.Server.dll"]
