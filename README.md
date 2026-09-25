# Deadworks

A server-side modding framework for [Deadlock](https://store.steampowered.com/app/1422450/Deadlock/).

> **Early development** — APIs are not finalized and will change without notice. We are not distributing prebuilt binaries at this time. Early users and contributors should build from source.

## Prerequisites

### Visual Studio

Install [Visual Studio 2026](https://visualstudio.microsoft.com/) with the following workloads:

- **Desktop development with C++**
- **.NET desktop development**

### .NET 10 SDK

Download the latest .NET 10.x.x SDK from [dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

After installing, locate the `nethost` static library. Note down this path for `local.props` later.

The path will look like: `C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\10.0.5\runtimes\win-x64\native`

### protobuf 3.21.8

The native layer statically links `libprotobuf.lib`. You need protobuf **3.21.8** headers and a Release build of the static library.

1. Clone the protobuf 3.21.8 source:
   ```
   git clone --branch v3.21.8 --depth 1 https://github.com/protocolbuffers/protobuf.git protobuf-3.21.8
   ```
2. Configure with CMake:
   ```
   cmake -B build -DCMAKE_BUILD_TYPE=Release -Dprotobuf_BUILD_TESTS=OFF -Dprotobuf_MSVC_STATIC_RUNTIME=ON
   ```
3. Build:
   ```
   cmake --build build --config Release
   ```
4. After a successful build, you should have `libprotobuf.lib` in `build/Release/`. Note the paths to:
   - `src/` - headers (used for `ProtobufIncludeDir` in `local.props`)
   - `build/Release/` - static library (used for `ProtobufLibDir` in `local.props`)

### Deadlock

Install [Deadlock](https://store.steampowered.com/app/1422450/Deadlock/) via Steam. You'll need the path to `game\bin\win64\` for deployment.

The path will look like: `C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\bin\win64`

## Building

1. Clone with submodules:
   ```
   git clone --recurse-submodules https://github.com/Deadworks-net/deadworks.git
   ```

2. Copy `local.props.example` to `local.props` and fill in your paths:
   - `ProtobufIncludeDir` — protobuf `src/` directory (e.g. `C:\protobuf-3.21.8\src`)
   - `ProtobufLibDir` — protobuf build output (e.g. `C:\protobuf-3.21.8\build\Release`)
   - `NetHostDir` — .NET `nethost` native directory (see above)
   - `DeadlockDir` — Deadlock `game\bin\win64` (optional, enables automatic post-build deploy)

3. Open `deadworks.slnx` in Visual Studio and build (x64 Release).

4. Run the built `deadworks.exe` from `<Deadlock>/game/bin/win64/` to start the Deadworks server.

5. Open your game and connect via console `connect localhost:27067`

## Hosting on Linux (Docker)

Deadlock has no Linux server, so the image runs the Windows build under Wine. It needs no Steam
client and no build toolchain: Deadworks comes from the GitHub release, and the server logs on to
Steam anonymously like any other dedicated server, so connecting players are authenticated by Steam.

You need Docker with Compose, ~40 GB of disk, and a Steam account that has Deadlock in its library
(only used to download the game files; a throwaway account is fine).

```
mkdir deadworks && cd deadworks
curl -fsSLO https://raw.githubusercontent.com/Deadworks-net/deadworks/main/docker/compose.yaml
curl -fsSL -o .env https://raw.githubusercontent.com/Deadworks-net/deadworks/main/docker/.env.example
# edit .env: STEAM_USERNAME, STEAM_PASSWORD
docker compose up -d && docker compose logs -f
```

The first start downloads the game into the `steam` volume; later starts only fetch updates. If
Steam Guard asks for a typed code, or you would rather scan a QR code than put a password in
`.env`, run `docker compose run --rm deadworks login` once. Then connect from the game console
with `connect <server-ip>:27015`. Only UDP 27015 needs to be open.

| I want to... | Do this |
| --- | --- |
| Name the server, set a password, change map or port | `SERVER_NAME`, `SERVER_PASSWORD`, `SERVER_MAP`, `SERVER_PORT` in `.env`, then `docker compose up -d` |
| Set cvars | put them in `./configs/server.cfg`; it is exec'd on every start |
| Install a plugin | drop its `.dll` (and any dependencies) into `./plugins`; it loads immediately, no restart |
| Add a custom map | drop its `.vpk` into `./maps` and restart (`docker compose restart`); load it with `SERVER_MAP` or `map <name>`. Players need the map too |
| Configure Deadworks or a plugin | edit the files that appear in `./configs` |
| Run a console command | `docker compose exec deadworks console status` |
| Open an interactive console | `docker compose exec deadworks console` (ctrl-d to leave) |
| Read the log | `docker compose logs -f` |
| Check it is up | `docker compose ps` shows `healthy` once the server answers queries |
| Update the game or Deadworks | `docker compose restart` (both are checked on every start) |
| Update the image itself (Wine, .NET) | `docker compose pull && docker compose up -d` |
| Use a remote RCON tool | set `RCON_PASSWORD` and uncomment the tcp port in `compose.yaml` |
| Reuse game files I already have | mount a Windows install of Deadlock at `/steam/game:ro`, leave `STEAM_USERNAME` empty; nothing is written to it |
| Pin a Deadworks version | `DEADWORKS_VERSION=v0.4.16` in `.env` (`image` for the one baked into the image) |
| Back up | `./configs`, `./plugins` and `./maps` are everything; the volumes can always be re-created |

### Several servers on one host

The game is never modified: each server runs from its own tree of symlinks into the install and
keeps everything it writes in its `data` volume (~0.8 GB). So any number of servers can share one
35 GB download. Use [`compose.multi.yaml`](docker/compose.multi.yaml) instead of `compose.yaml`;
it defines two servers, and adding one is copying a block and changing its name and port:

```
curl -fsSL -o compose.yaml https://raw.githubusercontent.com/Deadworks-net/deadworks/main/docker/compose.multi.yaml
docker compose up -d
docker compose exec one console status
```

Each server gets its own `./<name>/plugins` and `./<name>/configs`; `./maps` is shared. The game is only updated when
no server is using it, so to pick up a game update restart them all together
(`docker compose restart`); a server restarted on its own keeps running the installed build.

After a game update, Deadworks may need a new release before its hooks match the new build. When
that happens the server says so in the log (`Deadworks vX does not support Deadlock build N`),
shows as `unhealthy`, and waits: it checks for a new Deadworks release every 10 minutes and starts
by itself once there is one. There is nothing to do in the meantime; staying on the old game
build with `AUTO_UPDATE=0` does not help, because updated players cannot join it.

Cheat-protected cvars such as `sv_cheats` are reset by the engine when the map loads, so set those
through `console` rather than `server.cfg`.
