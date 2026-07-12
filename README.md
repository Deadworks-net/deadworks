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

1. Clone with submodules into (``C:\Program Files (x86)\Steam\steamapps\common\Deadlock``) : 
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
