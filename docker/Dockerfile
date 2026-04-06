# =============================================================================
# Stage 1: Cross-compile deadworks.exe from Linux using clang-cl + xwin
# =============================================================================
FROM ubuntu:24.04 AS native-builder

ENV DEBIAN_FRONTEND=noninteractive

# Install LLVM 20 from official apt repo (C++23 support), cmake, and tools
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget gnupg ca-certificates software-properties-common \
    cmake ninja-build unzip git python3 \
    && wget -qO- https://apt.llvm.org/llvm-snapshot.gpg.key | tee /etc/apt/trusted.gpg.d/apt.llvm.org.asc \
    && echo "deb http://apt.llvm.org/noble/ llvm-toolchain-noble-20 main" > /etc/apt/sources.list.d/llvm.list \
    && apt-get update && apt-get install -y --no-install-recommends \
    clang-20 lld-20 llvm-20 \
    && rm -rf /var/lib/apt/lists/*

# Create wrapper scripts for MSVC-compatible tools.
# clang-cl is just clang with --driver-mode=cl (argv[0] detection unreliable in Docker).
RUN printf '#!/bin/sh\nexec /usr/bin/clang-20 --driver-mode=cl "$@"\n' > /usr/local/bin/clang-cl && chmod +x /usr/local/bin/clang-cl \
    && printf '#!/bin/sh\nexec /usr/bin/lld-20 -flavor link "$@"\n' > /usr/local/bin/lld-link && chmod +x /usr/local/bin/lld-link \
    && ln -sf /usr/bin/llvm-lib-20 /usr/local/bin/llvm-lib \
    && ln -sf /usr/bin/llvm-rc-20 /usr/local/bin/llvm-rc

# Download xwin to get Windows SDK + MSVC CRT headers/libs
ARG XWIN_VERSION=0.6.5
RUN wget -qO /tmp/xwin.tar.gz \
    "https://github.com/Jake-Shadle/xwin/releases/download/${XWIN_VERSION}/xwin-${XWIN_VERSION}-x86_64-unknown-linux-musl.tar.gz" && \
    tar xzf /tmp/xwin.tar.gz -C /tmp && \
    mv /tmp/xwin-*/xwin /usr/local/bin/ && \
    rm -rf /tmp/xwin*

RUN xwin --accept-license splat --output /xwin

# Download nethost headers + lib from .NET Windows hosting pack (NuGet)
ARG DOTNET_VERSION=10.0.0
RUN wget -qO /tmp/nethost.nupkg \
    "https://www.nuget.org/api/v2/package/Microsoft.NETCore.App.Host.win-x64/${DOTNET_VERSION}" && \
    mkdir -p /nethost && \
    unzip -o /tmp/nethost.nupkg -d /tmp/nethost-pkg && \
    cp /tmp/nethost-pkg/runtimes/win-x64/native/nethost.h /nethost/ && \
    cp /tmp/nethost-pkg/runtimes/win-x64/native/coreclr_delegates.h /nethost/ && \
    cp /tmp/nethost-pkg/runtimes/win-x64/native/hostfxr.h /nethost/ && \
    cp /tmp/nethost-pkg/runtimes/win-x64/native/libnethost.lib /nethost/ && \
    rm -rf /tmp/nethost*

# Build protobuf 3.21.8 for Windows using clang-cl cross-compilation
# Download source tarball (more reliable than git clone in BuildKit)
RUN wget -qO /tmp/protobuf.tar.gz \
    "https://github.com/protocolbuffers/protobuf/archive/refs/tags/v3.21.8.tar.gz" && \
    tar xzf /tmp/protobuf.tar.gz -C / && \
    mv /protobuf-3.21.8 /protobuf-src && \
    rm /tmp/protobuf.tar.gz

COPY docker/toolchain-clangcl.cmake /toolchain-clangcl.cmake

RUN cmake -G Ninja \
    -S /protobuf-src/cmake \
    -B /protobuf-build \
    -DCMAKE_TOOLCHAIN_FILE=/toolchain-clangcl.cmake \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POLICY_DEFAULT_CMP0091=NEW \
    -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded \
    -Dprotobuf_BUILD_TESTS=OFF \
    -Dprotobuf_MSVC_STATIC_RUNTIME=ON && \
    cmake --build /protobuf-build --config Release

# Copy project source
WORKDIR /build
COPY sourcesdk sourcesdk
COPY vendor vendor
COPY protobuf protobuf
COPY config config
COPY docker/build-native.sh docker/build-native.sh
COPY deadworks deadworks

RUN chmod +x docker/build-native.sh && \
    ./docker/build-native.sh

# =============================================================================
# Stage 2: Build .NET managed layer
# =============================================================================

# Empty default for extra plugins — override with:
#   docker build --build-context extra-plugins=../my-plugins .
FROM busybox AS extra-plugins
WORKDIR /extra

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS managed-builder

WORKDIR /build
COPY managed managed
COPY game_exported game_exported

# Merge extra plugin sources into managed/plugins/.
# Each subdirectory should contain a .csproj referencing DeadworksManaged.Api.
RUN --mount=from=extra-plugins,target=/extra \
    cp -r /extra/. managed/plugins/ 2>/dev/null || true

RUN dotnet publish managed/DeadworksManaged.csproj \
    -c Release -o /artifacts/managed --no-self-contained

# Auto-discover and publish all plugins under managed/plugins/
RUN mkdir -p /artifacts/managed/plugins && \
    find managed/plugins -name '*.csproj' -print0 | \
    xargs -0 -r -I {} dotnet publish {} -c Release -o /artifacts/managed/plugins --no-self-contained

# =============================================================================
# Stage 3: Runtime image with Proton/Wine + SteamCMD
# =============================================================================
FROM cm2network/steamcmd:root AS runtime

RUN dpkg --add-architecture i386

RUN apt-get update && apt-get install -y --no-install-recommends \
    gosu \
    wget \
    unzip \
    lsof \
    python3 \
    netcat-openbsd \
    xvfb \
    procps \
    cabextract \
    libfreetype6 libfreetype6:i386 \
    libvulkan1 libvulkan1:i386 \
    libx11-6 libx11-6:i386 \
    libxext6 libxext6:i386 \
    libxrandr2 libxrandr2:i386 \
    libxcomposite1 libxcomposite1:i386 \
    libxcursor1 libxcursor1:i386 \
    libxi6 libxi6:i386 \
    libxfixes3 libxfixes3:i386 \
    libxrender1 libxrender1:i386 \
    libgl1 libgl1:i386 \
    libglib2.0-0 libglib2.0-0:i386 \
    libdbus-1-3 libdbus-1-3:i386 \
    libfontconfig1 libfontconfig1:i386 \
    libnss3 libnss3:i386 \
    libgdk-pixbuf-2.0-0 \
    libgtk-3-0 \
    libsdl2-2.0-0 \
    libgnutls30 libgnutls30:i386 \
    && rm -rf /var/lib/apt/lists/*

# Set up Steam compatibility directory structure
RUN mkdir -p \
    /home/steam/.steam/steam/compatibilitytools.d \
    /home/steam/.steam/steam/steamapps/compatdata/1422450/pfx \
    /home/steam/server \
    /opt/proton \
    /opt/deadworks && \
    chown -R steam:steam /home/steam/.steam /home/steam/server

# Copy build artifacts
COPY --from=native-builder /build/out/deadworks.exe /opt/deadworks/game/bin/win64/
COPY --from=native-builder /build/config/deadworks_mem.jsonc /opt/deadworks/game/citadel/cfg/
COPY --from=managed-builder /artifacts/managed /opt/deadworks/game/bin/win64/managed/

COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/entrypoint.sh

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
