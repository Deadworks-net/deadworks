#!/usr/bin/env bash
# Deadworks container entrypoint.
#
#   run      (default) update the game if credentials allow, deploy Deadworks, start the server
#   update   download/update the game files and exit
#   login    interactive Steam login, for accounts whose Steam Guard wants a typed code:
#              docker compose run --rm deadworks login
#   <cmd>    anything else is exec'd as-is (bash, wine ...)
#
# Two volumes. /steam is what comes from Steam and is the same for every server on the host: the
# Deadlock install (~35 GB), the Steamworks redist, the download account's login token. Only
# DepotDownloader ever writes to it, so any number of servers can mount the same one. /data is
# this one server: a tree of symlinks into the install, with Deadworks, the generated cfgs and
# whatever the engine writes laid over it as real files, plus the Wine prefix.
set -euo pipefail

APP_ID=1422450
STEAM_DIR=/steam
GAME_DIR="${GAME_DIR:-$STEAM_DIR/game}"
SERVER_DIR=/data/server
WIN64="$SERVER_DIR/game/bin/win64"
PUID="${PUID:-1000}"
PGID="${PGID:-1000}"

log() { printf '[deadworks-docker] %s\n' "$*"; }
die() { log "ERROR: $*" >&2; exit 1; }

# set -e on its own exits without a word; with a restart policy that is a silent crash loop.
set -E
trap 'log "ERROR: \"$BASH_COMMAND\" failed (entrypoint.sh line $LINENO)" >&2' ERR

# Started as root so that bind mounts owned by whoever made them on the host can be fixed up;
# everything after this block runs unprivileged. Wine refuses a prefix it does not own.
if [ "$(id -u)" = "0" ]; then
    mkdir -p "$GAME_DIR" "$STEAM_DIR/home" "$HOME" "$WINEPREFIX"
    for d in /data "$STEAM_DIR" "$GAME_DIR" "$STEAM_DIR/home" "$HOME" "$WINEPREFIX" /plugins /configs; do
        # An install mounted read-only (somebody's existing copy) is left exactly as it is.
        [ -w "$d" ] || continue
        [ "$(stat -c %u:%g "$d")" = "$PUID:$PGID" ] || chown -R "$PUID:$PGID" "$d"
    done
    exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups "$0" "$@"
fi

# `|| true`: on a first run there is no steam.inf yet, and under set -e/pipefail grep's failure
# would end the script before it gets to say what is wrong.
build_id() { { grep -h '^ClientVersion=' "$GAME_DIR/game/citadel/steam.inf" 2>/dev/null || true; } | tr -d '\r' | cut -d= -f2; }

# DepotDownloader rather than steamcmd: no 32-bit userland, and it only ever fetches the Windows
# depots. Deadlock cannot be downloaded anonymously; the account just has to have it in its
# library. -remember-password leaves a login token under its $HOME (in the /steam volume, so one
# login covers every server sharing it); after that STEAM_PASSWORD can be dropped.
depot_download() {
    [ -n "${STEAM_USERNAME:-}" ] || return 2
    local args=(-app "$APP_ID" -os windows -osarch 64 -dir "$GAME_DIR"
                -remember-password -max-downloads "${MAX_DOWNLOADS:-16}")
    if [ "${1:-}" = "-qr" ]; then
        # DepotDownloader refuses -qr together with -username; the token is stored under whichever
        # account scans the code, and later runs find it by STEAM_USERNAME.
        args+=(-qr); shift
    else
        args+=(-username "$STEAM_USERNAME")
        [ -n "${STEAM_PASSWORD:-}" ] && args+=(-password "$STEAM_PASSWORD")
    fi
    [ "${VALIDATE:-0}" = "1" ] && args+=(-validate)
    [ -n "${GAME_BRANCH:-}" ] && args+=(-branch "$GAME_BRANCH")
    HOME="$STEAM_DIR/home" /opt/depotdownloader/DepotDownloader "${args[@]}" "$@"
}

have_game() { [ -f "$GAME_DIR/game/bin/win64/engine2.dll" ]; }

# Servers sharing an install hold a shared lock on it for as long as they run, and an update takes
# it exclusively. So game files never change underneath a running server, and servers started
# together do not all download at once: one does, the rest wait for it. fd 9 is inherited by
# Wine, which keeps the lock held until the game process is really gone.
LOCKED=0
open_lock() {
    [ "$LOCKED" = "0" ] && [ -w "$GAME_DIR" ] || return 0
    exec 9>>"$GAME_DIR/.deadworks.lock"
    LOCKED=1
}

hold_game() {
    open_lock
    [ "$LOCKED" = "1" ] || return 0
    if ! flock -s -n 9; then
        log "another server is updating the game files; waiting for it to finish"
        flock -s 9
    fi
}

update_game() {
    local before; before=$(build_id)
    if [ -z "${STEAM_USERNAME:-}" ]; then
        have_game || die "no game files in $GAME_DIR and STEAM_USERNAME is not set.
       Set STEAM_USERNAME/STEAM_PASSWORD (any account with Deadlock in its library), or mount an
       existing Windows install of Deadlock at $GAME_DIR."
        log "STEAM_USERNAME not set; skipping update (game build ${before:-unknown})"
        return 0
    fi
    if [ ! -w "$GAME_DIR" ]; then
        log "$GAME_DIR is read-only; skipping update (game build ${before:-unknown})"
        return 0
    fi
    open_lock
    # The wait covers `docker compose restart`, where the other servers are on their way down.
    if ! flock -x -w "${1:-15}" 9; then
        log "game files are in use by another server; skipping the update check (to update, restart all servers sharing them together)"
        return 0
    fi
    if have_game; then
        log "checking for a Deadlock update (installed build ${before:-unknown})"
    else
        log "downloading Deadlock, ~35 GB"
    fi
    # No tty means nobody can answer a Steam Guard code prompt: fail instead of hanging. A mobile
    # app confirmation needs no input and still works here.
    local rc=0
    if [ -t 0 ]; then depot_download || rc=$?; else depot_download </dev/null || rc=$?; fi
    if [ "$rc" != "0" ]; then
        if ! have_game; then
            log "ERROR: download failed (see above). Check STEAM_USERNAME/STEAM_PASSWORD; if Steam" >&2
            log "       Guard wants a typed code, run: docker compose run --rm deadworks login" >&2
            flock -u 9
            # The restart policy would otherwise retry the login every few seconds, and Steam
            # rate-limits accounts that do that.
            [ -t 0 ] || { log "holding for 10 minutes before exiting" >&2; sleep 600; }
            exit 1
        fi
        log "WARNING: update failed (rc=$rc); starting the installed build ${before:-unknown}"
        return 0
    fi
    log "game build: $(build_id)"
}

# Real Steam without a Steam client. The game's own steam_api64.dll only needs steamclient64.dll
# (and its two dependencies) beside the exe; the server then logs on anonymously and validates
# every connecting player's ticket against Steam, so SteamIDs can be trusted. The DLLs are the
# Steamworks SDK Redist (app 1007), which needs no account.
install_steamclient() {
    local dir="$STEAM_DIR/redist" f
    if [ "${AUTO_UPDATE:-1}" = "1" ] || [ ! -f "$dir/steamclient64.dll" ]; then
        # flock: servers started together would otherwise download into the same directory.
        HOME="$STEAM_DIR/home" flock "$STEAM_DIR/redist.lock" \
            /opt/depotdownloader/DepotDownloader -app 1007 -os windows -osarch 64 -dir "$dir" >/dev/null 2>&1 \
            || log "WARNING: could not refresh the Steamworks redist"
    fi
    [ -f "$dir/steamclient64.dll" ] || die "no steamclient64.dll: downloading the Steamworks redist (app 1007) failed"
    # Copied, not linked: a refresh must not rewrite a DLL another running server has mapped.
    for f in steamclient64.dll tier0_s64.dll vstdlib_s64.dll; do
        cmp -s "$dir/$f" "$WIN64/$f" || cp -f --remove-destination "$dir/$f" "$WIN64/$f"
    done
    rm -f "$WIN64/steam_appid.txt"
    echo "$APP_ID" > "$WIN64/steam_appid.txt"
}

# This server's view of the install: real directories, one symlink per shipped file (~4000, well
# under a second). Wine follows them transparently and the engine still finds everything relative
# to the exe, but whatever it or Deadworks writes lands in /data instead of in the shared install.
# Links are per file because Wine reports a symlinked directory as a reparse point and a symlinked
# file as a plain file. Rebuilt on every start, so files a game update added or removed follow.
build_tree() {
    mkdir -p "$SERVER_DIR"
    find "$SERVER_DIR" -type l -lname "$GAME_DIR/*" -delete
    # The skipped paths are DepotDownloader's bookkeeping and, if the install is a copy somebody
    # already ran Deadworks from, the directories this script owns.
    perl -e '
        my ($src, $dst) = (shift, shift);
        my %skip = map { $_ => 1 } @ARGV;
        sub walk {
            my $rel = shift;
            opendir(my $dh, "$src/$rel") or die "$src/$rel: $!\n";
            for my $e (sort readdir $dh) {
                next if $e eq "." || $e eq "..";
                my $p = $rel eq "" ? $e : "$rel/$e";
                next if $skip{$p};
                if (-d "$src/$p") { -d "$dst/$p" or mkdir "$dst/$p" or die "$dst/$p: $!\n"; walk($p); }
                # A real file already there belongs to this server and wins.
                elsif (!lstat("$dst/$p")) { symlink("$src/$p", "$dst/$p") or die "$dst/$p: $!\n"; }
            }
        }
        walk("");
    ' "$GAME_DIR" "$SERVER_DIR" .DepotDownloader .deadworks.lock game/bin/win64/managed game/bin/win64/configs
}

# The newest release tag, or nothing if GitHub cannot be reached. The releases/latest redirect
# rather than the API: unauthenticated API calls get rate-limited.
latest_deadworks() {
    local url
    url=$(curl -fsSLI -m 20 -o /dev/null -w '%{url_effective}' \
        "https://github.com/${DEADWORKS_REPO:-Deadworks-net/deadworks}/releases/latest" 2>/dev/null) || return 0
    case "$url" in */releases/tag/*) echo "${url##*/}" ;; esac
}

# Which Deadworks to run, by DEADWORKS_VERSION:
#   latest  (default) follow GitHub releases, checked at every start. A game update can leave
#           Deadworks unable to hook the new build until a release catches up; this way the fix
#           arrives with a restart instead of waiting for somebody to pull a new image.
#   v1.2.3  pin that release.
#   image   the copy baked into the image; never goes online.
# Releases are cached in /steam, so servers sharing it download each one once. GitHub being down
# must not keep a working server from starting, so "latest" falls back to what is on disk.
DEADWORKS_SRC=/opt/deadworks
DEADWORKS_TAG=$(cat /opt/deadworks/.version 2>/dev/null || echo unknown)
resolve_deadworks() {
    local want="${DEADWORKS_VERSION:-latest}" cache="$STEAM_DIR/deadworks" tag
    [ "$want" = "image" ] && return 0
    mkdir -p "$cache"
    tag="$want"
    if [ "$want" = "latest" ]; then
        tag=$(latest_deadworks)
        if [ -n "$tag" ]; then
            echo "$tag" > "$cache/latest"
        else
            tag=$(cat "$cache/latest" 2>/dev/null || echo "$DEADWORKS_TAG")
            log "WARNING: could not check GitHub for a newer Deadworks; staying on $tag"
        fi
    fi
    [ "$tag" = "$DEADWORKS_TAG" ] && return 0

    if ! fetch_deadworks "$tag" "$cache"; then
        [ "$want" = "latest" ] || die "could not download Deadworks $tag (DEADWORKS_VERSION); check the tag"
        log "WARNING: could not download Deadworks $tag; using $DEADWORKS_TAG from the image"
        return 0
    fi
    DEADWORKS_SRC="$cache/$tag"
    DEADWORKS_TAG="$tag"
    # Keep the three most recent releases.
    find "$cache" -mindepth 1 -maxdepth 1 -type d ! -name '.*' -printf '%T@ %p\n' | sort -rn | tail -n +4 \
        | cut -d' ' -f2- | xargs -r -d '\n' rm -rf --
}

fetch_deadworks() {
    local tag="$1" cache="$2" tmp rc=0
    (
        flock 8   # servers started together: one downloads, the others find it done
        [ -f "$cache/$tag/game/bin/win64/deadworks.exe" ] && exit 0
        log "downloading Deadworks $tag"
        tmp=$(mktemp -d "$cache/.dl.XXXXXX")
        curl -fsSL -m 300 -o "$tmp/dw.zip" \
            "https://github.com/${DEADWORKS_REPO:-Deadworks-net/deadworks}/releases/download/$tag/deadworks-$tag.zip" \
            && unzip -q "$tmp/dw.zip" -d "$tmp/x" && [ -f "$tmp/x/game/bin/win64/deadworks.exe" ] \
            && rm -rf "${cache:?}/$tag" && mv "$tmp/x" "$cache/$tag" || rc=$?
        rm -rf "$tmp"
        exit "$rc"
    ) 8>"$cache/.lock" || return 1
}

deploy_deadworks() {
    local list="$SERVER_DIR/.deadworks-files"
    mkdir -p "$SERVER_DIR/game"
    # The previous release's files go first, so switching versions leaves nothing stale behind.
    if [ -f "$list" ]; then (cd "$SERVER_DIR/game" && xargs -r -d '\n' rm -f -- < "$list"); fi
    (cd "$DEADWORKS_SRC/game" && find . -type f ! -path './bin/win64/managed/plugins/*') > "$list"
    # --remove-destination: never write through a link into the shared install.
    (cd "$DEADWORKS_SRC/game" && xargs -d '\n' cp --parents --remove-destination -t "$SERVER_DIR/game" -- < "$list")
    # /plugins and /configs are the mount points people actually touch; the real locations are
    # five directories deep inside the game install.
    link_dir /plugins "$WIN64/managed/plugins"
    link_dir /configs "$WIN64/configs"
    log "deployed Deadworks $DEADWORKS_TAG"
}

link_dir() {
    local target="$1" link="$2"
    [ -L "$link" ] && return 0
    if [ -d "$link" ]; then
        cp -rn "$link/." "$target/" 2>/dev/null || true
        rm -rf "$link"
    fi
    ln -s "$target" "$link"
}

init_wine() {
    [ -f "$WINEPREFIX/system.reg" ] && return 0
    log "creating Wine prefix (one-time, ~30 s)"
    # mscoree is disabled for this one command only, to keep wineboot from trying to install
    # wine-mono. It must stay enabled at runtime: Wine's loader goes through it to map any DLL
    # with a CLR header, which is how CoreCLR loads every managed assembly.
    WINEDLLOVERRIDES="mscoree,$WINEDLLOVERRIDES" wineboot --init >/dev/null 2>&1 || true
    wineserver -w
}

# Settings go through a generated cfg rather than the command line for two reasons: deadworks.exe
# re-joins its argv with spaces, so a server name with a space in it would not survive, and
# passwords on a command line end up in `docker logs` and `ps`.
write_server_cfg() {
    local cfg="$SERVER_DIR/game/citadel/cfg" pw_file=/data/rcon_password
    # RCON is what `console` talks to. Without RCON_PASSWORD a random one is made once and kept,
    # so the console works out of the box and nothing guessable ever listens.
    if [ -n "${RCON_PASSWORD:-}" ]; then
        printf '%s\n' "$RCON_PASSWORD" > "$pw_file"
    elif [ ! -s "$pw_file" ]; then
        head -c 18 /dev/urandom | base64 | tr -d '/+=' > "$pw_file"
    fi
    chmod 600 "$pw_file"

    {
        echo "// Generated by the container on every start; edit .env or /configs/server.cfg instead."
        echo "hostname \"$(printf '%s' "${SERVER_NAME:-Deadworks Server}" | tr -d '";\n')\""
        echo "sv_password \"$(printf '%s' "${SERVER_PASSWORD:-}" | tr -d '";\n')\""
        echo "rcon_password \"$(tr -d '";\n' < "$pw_file")\""
    } > "$cfg/deadworks_docker.cfg"
    chmod 600 "$cfg/deadworks_docker.cfg"

    # The operator's own cfg, from the mount they can actually reach. Copied because the engine
    # only execs from inside the game's cfg directory.
    if [ -f /configs/server.cfg ]; then
        cp -f /configs/server.cfg "$cfg/deadworks_user.cfg"
    else
        : > "$cfg/deadworks_user.cfg"
    fi
}

# Crash dumps land beside the exe and are never cleaned up by the engine.
prune_dumps() {
    find "$WIN64" -maxdepth 1 -name '*.mdmp' -printf '%T@ %p\n' | sort -rn | tail -n +6 | cut -d' ' -f2- \
        | xargs -r -d '\n' rm -f --
}

run_server() {
    local args=(-dedicated -console -dev -insecure -allow_no_lobby_connect -usercon
        +tv_citadel_auto_record 0 +spec_replay_enable 0 +tv_enable 0 +citadel_upload_replay_enabled 0
        +hostport "${SERVER_PORT:-27015}" +exec deadworks_docker.cfg +exec deadworks_user.cfg
        +map "${SERVER_MAP:-dl_midtown}")
    # shellcheck disable=SC2206  # word splitting is the point
    [ -n "${EXTRA_ARGS:-}" ] && args+=($EXTRA_ARGS)

    log "starting: deadworks.exe ${args[*]}"
    rm -f /tmp/deadworks-starting /tmp/deadworks-unsupported
    cd "$WIN64"
    # The server belongs to the wineserver, not to this shell: signalling `wine` alone leaves it
    # running, so a container stop has to go through wineserver -k.
    local stopping=0 started=$SECONDS
    # A server that stays up for five minutes on a build is the evidence that Deadworks works on
    # that build; it is how a later crash right after a game update gets recognised for what it is.
    ( sleep 300; build_id > /data/last_good_build ) &
    local good_pid=$!
    trap 'stopping=1; log "stopping"; kill "$good_pid" 2>/dev/null; wineserver -k; wait' TERM INT
    if [ -t 0 ]; then
        # With a terminal (docker compose run), hand it to Wine: stdin is then a real console and
        # the complaint below never happens. The explicit <&0 matters, a background job otherwise
        # gets /dev/null. Typing commands into it is untested.
        wine deadworks.exe "${args[@]}" <&0 &
    else
        # Detached: stdin is not a Windows console, and the engine's text console complains about
        # that once per tick (~60 lines/s). Nothing else is filtered. The same pass spots
        # Deadworks giving up on its signatures; releases that predate its exit code 78 only say so.
        wine deadworks.exe "${args[@]}" > >(perl -ne '
            BEGIN { $| = 1 }
            next if /CTextConsoleWin::GetLine/;
            if (/Failed to find (patch )?signature/) { open(my $f, ">", "/tmp/deadworks-unsupported"); }
            print;') 2>&1 &
    fi
    # `|| rc=$?` keeps a stop (143) or a server crash from tripping the ERR trap above.
    local rc=0
    wait $! || rc=$?
    kill "$good_pid" 2>/dev/null || true
    [ "$stopping" = "1" ] && exit "$rc"
    [ "$rc" = "0" ] || sleep 1   # let the output filter above finish with the last lines

    local build last_good; build=$(build_id); last_good=$(cat /data/last_good_build 2>/dev/null || true)
    if [ "$rc" = "78" ] || [ -e /tmp/deadworks-unsupported ]; then
        log "ERROR: Deadworks $DEADWORKS_TAG does not support Deadlock build $build: its signatures no longer match (see above)." >&2
        wait_for_release
    elif [ "$rc" != "0" ] && [ $((SECONDS - started)) -lt 300 ] && [ -n "$last_good" ] && [ "$last_good" != "$build" ]; then
        log "ERROR: the server exited (code $rc) within $((SECONDS - started)) s of starting, and the game has been updated" >&2
        log "       from build $last_good to $build since it last ran properly. Deadworks $DEADWORKS_TAG most likely does not support build $build." >&2
        wait_for_release
    fi
    exit "$rc"
}

# Valve updated the game and Deadworks has not caught up. Restarting cannot fix that, and every
# restart is another Steam login, so sit here instead (unhealthy, since nothing answers queries)
# until a newer Deadworks exists, then exit and let the restart policy start over with it.
wait_for_release() {
    trap 'exit 0' TERM INT
    local i tag poll="${RELEASE_CHECK_INTERVAL:-600}"
    if [ "${DEADWORKS_VERSION:-latest}" != "latest" ]; then
        log "       DEADWORKS_VERSION is pinned to ${DEADWORKS_VERSION}; set it to a release that supports this build, or to latest." >&2
        sleep "$poll" & wait $!
        exit 1
    fi
    log "       Nothing needs fixing on your side. Checking for a new Deadworks release every 10 minutes;" >&2
    log "       the server starts by itself once there is one. https://github.com/${DEADWORKS_REPO:-Deadworks-net/deadworks}/releases" >&2
    for i in 1 2 3 4 5 6; do
        sleep "$poll" & wait $!
        tag=$(latest_deadworks)
        if [ -n "$tag" ] && [ "$tag" != "$DEADWORKS_TAG" ]; then
            log "Deadworks $tag is out; restarting with it"
            exit 1
        fi
    done
    # Once an hour go round the whole start sequence anyway; Valve may have shipped another build.
    exit 1
}

case "${1:-run}" in
    run)
        # Tells the HEALTHCHECK that a long first download is not a dead server.
        touch /tmp/deadworks-starting
        if [ "${AUTO_UPDATE:-1}" = "1" ]; then update_game; fi
        hold_game
        have_game || die "no game files in $GAME_DIR"
        build_tree
        install_steamclient
        resolve_deadworks
        deploy_deadworks
        write_server_cfg
        prune_dumps
        init_wine
        run_server
        ;;
    update)
        # Waits longer than a server start does, but not forever: with servers running it says so.
        update_game 60
        ;;
    login)
        [ -n "${STEAM_USERNAME:-}" ] || die "set STEAM_USERNAME first"
        [ -t 0 ] || die "login needs a terminal: docker compose run --rm deadworks login"
        # -manifest-only: authenticate and cache the token without starting the 35 GB download.
        # With no password set, log in by scanning a QR code with the Steam mobile app instead.
        # It writes manifest listings into its -dir, so that is a scratch directory, not the install.
        scratch=$(mktemp -d)
        if [ -n "${STEAM_PASSWORD:-}" ]; then
            GAME_DIR="$scratch" depot_download -manifest-only
        else
            log "scan the code with the Steam mobile app, signed in as $STEAM_USERNAME"
            GAME_DIR="$scratch" depot_download -qr -manifest-only
        fi
        rm -rf "$scratch"
        log "login token cached; start the server with: docker compose up -d"
        ;;
    *)
        exec "$@"
        ;;
esac
