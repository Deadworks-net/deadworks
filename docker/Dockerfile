# Deadworks dedicated server for Linux hosts.
#
# Deadlock ships no Linux server, so this runs the Windows build under 64-bit Wine. There is no
# Steam client in here: like any dedicated server it only needs Valve's steamclient DLLs and logs on
# to Steam anonymously. Those DLLs and the game files are Valve's to distribute, so neither is baked
# in; both are downloaded into the /steam volume on first start (see entrypoint.sh).
#
#   docker build -f docker/Dockerfile docker/
#   docker build -f docker/Dockerfile --build-arg DEADWORKS_VERSION=v0.4.16 docker/

FROM debian:trixie-slim AS fetch

RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl unzip \
    && rm -rf /var/lib/apt/lists/*

# "latest" or a release tag (v0.4.16). The zip is laid out relative to the Deadlock install root.
# This baked-in copy is the fallback: by default the entrypoint fetches the newest release at
# start, so that a server recovers from a game update with a restart rather than an image pull.
ARG DEADWORKS_VERSION=latest
ARG DEADWORKS_REPO=Deadworks-net/deadworks
RUN set -eux; \
    tag="$DEADWORKS_VERSION"; \
    if [ "$tag" = "latest" ]; then \
        # The releases/latest redirect, not the API: unauthenticated API calls get rate-limited.
        tag=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/${DEADWORKS_REPO}/releases/latest"); \
        tag="${tag##*/}"; \
    fi; \
    url="https://github.com/${DEADWORKS_REPO}/releases/download/${tag}/deadworks-${tag}.zip"; \
    curl -fsSL -o /tmp/deadworks.zip "$url"; \
    mkdir -p /out/deadworks; unzip -q /tmp/deadworks.zip -d /out/deadworks; \
    echo "$tag" > /out/deadworks/.version; \
    test -f /out/deadworks/game/bin/win64/deadworks.exe

# The Windows runtime: it is loaded by deadworks.exe inside Wine, not by anything native.
ARG DOTNET_VERSION=10.0.12
RUN set -eux; \
    curl -fsSL -o /tmp/dotnet.zip "https://builds.dotnet.microsoft.com/dotnet/Runtime/${DOTNET_VERSION}/dotnet-runtime-${DOTNET_VERSION}-win-x64.zip"; \
    mkdir -p /out/dotnet; unzip -q /tmp/dotnet.zip -d /out/dotnet; \
    test -d /out/dotnet/host/fxr

ARG DEPOTDOWNLOADER_VERSION=3.4.0
RUN set -eux; \
    curl -fsSL -o /tmp/dd.zip "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_${DEPOTDOWNLOADER_VERSION}/DepotDownloader-linux-x64.zip"; \
    mkdir -p /out/depotdownloader; unzip -q /tmp/dd.zip -d /out/depotdownloader; \
    chmod +x /out/depotdownloader/DepotDownloader


FROM debian:trixie-slim

# wine64 alone: the server and everything it loads is 64-bit, so the i386 half of Wine (and the
# multiarch it drags in) is dead weight. libicu is for DepotDownloader, curl and unzip for fetching
# Deadworks releases at start.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates wine wine64 libicu76 tini curl unzip \
    && rm -rf /var/lib/apt/lists/*

COPY --from=fetch /out/deadworks /opt/deadworks
COPY --from=fetch /out/dotnet /opt/dotnet-win
COPY --from=fetch /out/depotdownloader /opt/depotdownloader
COPY --chmod=755 entrypoint.sh /usr/local/bin/entrypoint.sh
COPY --chmod=755 console.pl /usr/local/bin/console

RUN useradd -m -u 1000 -s /bin/bash deadworks && mkdir -p /steam /data /plugins /configs \
    && chown deadworks:deadworks /steam /data /plugins /configs

ENV WINEPREFIX=/data/wineprefix \
    WINEARCH=win64 \
    WINEDEBUG=-all \
    WINEDLLOVERRIDES="mshtml,winemenubuilder.exe=d" \
    DOTNET_ROOT="Z:\\opt\\dotnet-win" \
    DOTNET_EnableWriteXorExecute=0 \
    XDG_RUNTIME_DIR=/tmp \
    HOME=/data/home

# /steam: the Deadlock install and Steam login token, shareable between servers. /data: one server.
VOLUME ["/steam", "/data"]
# udp is the game, tcp on the same port is RCON.
EXPOSE 27015/udp 27015/tcp

# An A2S_INFO query, i.e. what a server browser would see.
HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=3 CMD ["console", "--ping"]

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint.sh"]
CMD ["run"]
