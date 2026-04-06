#!/bin/bash
set -euo pipefail

# =============================================================================
# Phase 0: Environment variables
# =============================================================================
APP_ID=1422450
STEAM_LOGIN="${STEAM_LOGIN:?STEAM_LOGIN is required}"
STEAM_PASSWORD="${STEAM_PASSWORD:?STEAM_PASSWORD is required}"
SERVER_PORT="${SERVER_PORT:-27015}"
SERVER_MAP="${SERVER_MAP:-dl_midtown}"
SERVER_PASSWORD="${SERVER_PASSWORD:-}"
RCON_PASSWORD="${RCON_PASSWORD:-}"
TV_ENABLE="${TV_ENABLE:-0}"
TV_DELAY="${TV_DELAY:-0}"
TV_BROADCAST_URL="${TV_BROADCAST_URL:-http://hltv-relay:3000/publish}"
TV_BROADCAST_AUTH="${TV_BROADCAST_AUTH:-}"
PROTON_VERSION="${PROTON_VERSION:-GE-Proton10-33}"
DOTNET_VERSION="${DOTNET_VERSION:-10.0.0}"
DEADWORKS_ARGS="${DEADWORKS_ARGS:-}"

INSTALL_DIR="/home/steam/server"
PROTON_DIR="/opt/proton"
STEAM_PATH="/home/steam/.steam/steam"
COMPAT_DATA="${STEAM_PATH}/steamapps/compatdata/${APP_ID}"
PFXDIR="${COMPAT_DATA}/pfx"
WIN64_DIR="${INSTALL_DIR}/game/bin/win64"
REDIST_DIR="${STEAM_PATH}/steamapps/common/Steamworks SDK Redist"

# =============================================================================
# Phase 1: Proton setup
# =============================================================================
if [ ! -f "${PROTON_DIR}/proton" ]; then
    echo "[phase 1] Downloading ${PROTON_VERSION}..."
    wget -qO- "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/${PROTON_VERSION}/${PROTON_VERSION}.tar.gz" \
        | tar xzf - -C "${PROTON_DIR}" --strip-components=1
    ln -sfn "${PROTON_DIR}" "${STEAM_PATH}/compatibilitytools.d/${PROTON_VERSION}"
    echo "[phase 1] Proton installed."
else
    echo "[phase 1] Proton already cached."
fi

WINE64="${PROTON_DIR}/files/bin/wine64"

# =============================================================================
# Phase 2: Download game files via SteamCMD
# =============================================================================
echo "[phase 2] Updating Deadlock server files..."
mkdir -p "${COMPAT_DATA}"

MAX_RETRIES=3
for attempt in $(seq 1 $MAX_RETRIES); do
    echo "[phase 2] SteamCMD attempt ${attempt}/${MAX_RETRIES}..."
    gosu steam /home/steam/steamcmd/steamcmd.sh \
        +@sSteamCmdForcePlatformType windows \
        +force_install_dir "$INSTALL_DIR" \
        +login "$STEAM_LOGIN" "$STEAM_PASSWORD" \
        +app_update "$APP_ID" \
        +quit && break
    echo "[phase 2] WARNING: attempt ${attempt} failed, retrying..."
    sleep 5
done

if [ ! -f "${WIN64_DIR}/deadlock.exe" ]; then
    echo "[phase 2] ERROR: deadlock.exe not found after ${MAX_RETRIES} attempts"
    exit 1
fi
echo "[phase 2] Game files verified."

# Download Steam client DLLs
STEAM_CLIENT_DIR="/home/steam/steam_client"
if [ ! -f "${STEAM_CLIENT_DIR}/steamclient64.dll" ]; then
    echo "[phase 2] Downloading Windows Steam client DLLs..."
    mkdir -p "${STEAM_CLIENT_DIR}"
    gosu steam /home/steam/steamcmd/steamcmd.sh \
        +@sSteamCmdForcePlatformType windows \
        +force_install_dir "${STEAM_CLIENT_DIR}" \
        +login anonymous \
        +app_update 1007 validate \
        +quit || true
fi

# =============================================================================
# Phase 3: Wine prefix initialization
# =============================================================================
Xvfb :99 -screen 0 640x480x24 &
XVFB_PID=$!
sleep 1

PROTON_MARKER="${PFXDIR}/.proton_marker"
if [ ! -f "${PROTON_MARKER}" ]; then
    echo "[phase 3] Initializing Wine prefix..."
    rm -rf "${PFXDIR}"
    gosu steam mkdir -p "${PFXDIR}"
    gosu steam bash -c "
        export WINEPREFIX='${PFXDIR}'
        export WINEDLLPATH='${PROTON_DIR}/files/lib64/wine/x86_64-windows:${PROTON_DIR}/files/lib64/wine/x86_64-unix'
        export DISPLAY=:99
        export WINEDEBUG=-all
        '${WINE64}' wineboot --init 2>&1
    " || true
    touch "${PROTON_MARKER}"
    echo "[phase 3] Wine prefix initialized."
else
    echo "[phase 3] Wine prefix already initialized."
fi

# Install Steam client DLLs
for dll in steamclient64.dll steamclient.dll; do
    SRC="${REDIST_DIR}/${dll}"
    if [ -f "$SRC" ]; then
        mkdir -p "${PFXDIR}/drive_c/Program Files (x86)/Steam"
        cp -f "$SRC" "${PFXDIR}/drive_c/Program Files (x86)/Steam/${dll}"
        cp -f "$SRC" "${WIN64_DIR}/${dll}"
        cp -f "$SRC" "${PFXDIR}/drive_c/windows/system32/${dll}"
    fi
done

# =============================================================================
# Phase 4: .NET 10 Windows runtime installation
# =============================================================================
DOTNET_WINE_DIR="${PFXDIR}/drive_c/Program Files/dotnet"
DOTNET_MARKER="${PFXDIR}/.dotnet_${DOTNET_VERSION}_marker"
DOTNET_CACHE="/opt/dotnet-cache"

if [ ! -f "${DOTNET_MARKER}" ]; then
    echo "[phase 4] Installing .NET ${DOTNET_VERSION} Windows runtime..."

    DOTNET_ZIP="${DOTNET_CACHE}/dotnet-runtime-${DOTNET_VERSION}-win-x64.zip"
    if [ ! -f "${DOTNET_ZIP}" ]; then
        mkdir -p "${DOTNET_CACHE}"
        wget -qO "${DOTNET_ZIP}" \
            "https://dotnetcli.azureedge.net/dotnet/Runtime/${DOTNET_VERSION}/dotnet-runtime-${DOTNET_VERSION}-win-x64.zip"
    fi

    mkdir -p "${DOTNET_WINE_DIR}"
    unzip -qo "${DOTNET_ZIP}" -d "${DOTNET_WINE_DIR}"

    # Verify hostfxr is present
    if ls "${DOTNET_WINE_DIR}/host/fxr/"*/hostfxr.dll 1>/dev/null 2>&1; then
        echo "[phase 4] .NET runtime installed (hostfxr.dll found)."
    else
        echo "[phase 4] WARNING: hostfxr.dll not found after extraction!"
    fi

    touch "${DOTNET_MARKER}"
else
    echo "[phase 4] .NET ${DOTNET_VERSION} already installed."
fi

# =============================================================================
# Phase 5: Deploy deadworks into game directory
# =============================================================================
echo "[phase 5] Deploying deadworks..."

DEADWORKS_SRC="/opt/deadworks"

# Copy deadworks.exe
cp -f "${DEADWORKS_SRC}/game/bin/win64/deadworks.exe" "${WIN64_DIR}/"

# Copy managed layer
mkdir -p "${WIN64_DIR}/managed/plugins"
cp -rf "${DEADWORKS_SRC}/game/bin/win64/managed/"* "${WIN64_DIR}/managed/"

# Copy config
mkdir -p "${INSTALL_DIR}/game/citadel/cfg"
cp -f "${DEADWORKS_SRC}/game/citadel/cfg/deadworks_mem.jsonc" "${INSTALL_DIR}/game/citadel/cfg/"

# Write steam_appid.txt
echo "$APP_ID" > "${WIN64_DIR}/steam_appid.txt"
echo "$APP_ID" > "${INSTALL_DIR}/game/citadel/steam_appid.txt"

# Fix ownership
chown -R steam:steam "${WIN64_DIR}" "${INSTALL_DIR}/game/citadel"
chown -R steam:steam "${PFXDIR}"

echo "[phase 5] Deadworks deployed."
ls -la "${WIN64_DIR}/deadworks.exe"
ls -la "${WIN64_DIR}/managed/"

# =============================================================================
# Phase 6: Launch deadworks server
# =============================================================================

# Build server arguments (matches startup.cpp defaults but allows override)
SERVER_ARGS="-dedicated -console -dev -insecure -allow_no_lobby_connect"
SERVER_ARGS="${SERVER_ARGS} +hostport ${SERVER_PORT} +map ${SERVER_MAP}"

if [ "$TV_ENABLE" = "1" ]; then
    SERVER_ARGS="${SERVER_ARGS} +tv_enable 1 +tv_broadcast 1 +tv_maxclients 0 +tv_delay ${TV_DELAY}"
    SERVER_ARGS="${SERVER_ARGS} +tv_broadcast_url ${TV_BROADCAST_URL}"
    if [ -n "$TV_BROADCAST_AUTH" ]; then
        SERVER_ARGS="${SERVER_ARGS} +tv_broadcast_origin_auth ${TV_BROADCAST_AUTH}"
    fi
else
    SERVER_ARGS="${SERVER_ARGS} +tv_enable 0"
fi
SERVER_ARGS="${SERVER_ARGS} +tv_citadel_auto_record 0 +spec_replay_enable 0 +citadel_upload_replay_enabled 0"

if [ -n "$SERVER_PASSWORD" ]; then
    SERVER_ARGS="${SERVER_ARGS} +sv_password ${SERVER_PASSWORD}"
fi

if [ -n "$RCON_PASSWORD" ]; then
    SERVER_ARGS="${SERVER_ARGS} +rcon_password ${RCON_PASSWORD}"
fi

if [ -n "$DEADWORKS_ARGS" ]; then
    SERVER_ARGS="${SERVER_ARGS} ${DEADWORKS_ARGS}"
fi

# Clear stale logs
rm -f "${INSTALL_DIR}/game/citadel/console.log"

echo "[phase 6] Starting deadworks server on port ${SERVER_PORT}..."
echo "[phase 6] Args: ${SERVER_ARGS}"

cat > /tmp/run_server.sh << SERVSCRIPT
#!/bin/bash
export STEAM_COMPAT_DATA_PATH='${COMPAT_DATA}'
export STEAM_COMPAT_CLIENT_INSTALL_PATH='${STEAM_PATH}'
export SteamAppId=${APP_ID}
export SteamGameId=${APP_ID}
export DISPLAY=:99
export WINEDEBUG=warn+module,err+all
export DOTNET_ROOT='C:\\Program Files\\dotnet'
cd '${WIN64_DIR}'
'${PROTON_DIR}/proton' run ./deadworks.exe ${SERVER_ARGS} 2>&1
SERVSCRIPT
chmod +x /tmp/run_server.sh

gosu steam bash /tmp/run_server.sh
EXIT_CODE=$?

# =============================================================================
# Phase 7: Cleanup and log collection
# =============================================================================
kill "${XVFB_PID}" 2>/dev/null || true

echo "=== Server exited with code ${EXIT_CODE} ==="

echo "--- Steam stderr ---"
cat "${STEAM_PATH}/logs/stderr.txt" 2>/dev/null || echo "(no stderr log)"

echo "--- Game console log ---"
CONSOLE_LOG="${INSTALL_DIR}/game/citadel/console.log"
if [ -f "$CONSOLE_LOG" ]; then
    tail -200 "$CONSOLE_LOG"
else
    echo "(no console.log)"
fi

echo "--- Proton logs ---"
find /home/steam /tmp -name "*.log" -newer /proc/1/cmdline 2>/dev/null | while read -r f; do
    echo "=== $f ==="
    tail -50 "$f"
done

exit $EXIT_CODE
