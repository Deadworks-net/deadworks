#!/bin/bash
set -euo pipefail

XWIN="/xwin"
PROTOBUF_INC="/protobuf-src/src"
PROTOBUF_LIB="/protobuf-build"
NETHOST="/nethost"
SRC="deadworks/src"
OUT="out"
OBJ="obj"

mkdir -p "$OUT" "$OBJ"

TARGET="--target=x86_64-pc-windows-msvc"

WARN_FLAGS=(
    "-Wno-unused-command-line-argument"
    "-Wno-microsoft-include"
    "-Wno-pragma-pack"
    "-Wno-ignored-pragma-intrinsic"
    "-Wno-unknown-pragmas"
    "-Wno-ignored-attributes"
    "-Wno-deprecated-declarations"
    "-Wno-microsoft-exception-spec"
    "-Wno-expansion-to-defined"
    "-Wno-nonportable-include-path"
    "-Wno-inconsistent-missing-override"
)

XWIN_INCLUDES=(
    "/imsvc${XWIN}/crt/include"
    "/imsvc${XWIN}/sdk/include/ucrt"
    "/imsvc${XWIN}/sdk/include/um"
    "/imsvc${XWIN}/sdk/include/shared"
)

PROJECT_INCLUDES=(
    /Iprotobuf
    "/I${PROTOBUF_INC}"
    /Isourcesdk/public/tier1
    /Isourcesdk/public/tier0
    /Isourcesdk/public/appframework
    /Isourcesdk/public
    /Isourcesdk/common
    /Isourcesdk/public/mathlib
    /Isourcesdk/game/shared
    /Isourcesdk/public/entity2
    /Isourcesdk/public/engine
    /Ivendor
    "/I${NETHOST}"
)

BASE_DEFINES=(
    /DCOMPILER_MSVC
    /DCOMPILER_MSVC64
    /DPLATFORM_64BITS
    /DX64BITS
    /DDEADLOCK
    /DNDEBUG
    /D_CONSOLE
    /D_CRT_SECURE_NO_WARNINGS
    /DWIN32_LEAN_AND_MEAN
)

# ── Protobuf (C++17, standard clang-cl) ──
PROTOBUF_FLAGS=(
    $TARGET /EHsc /std:c++17 /MT /O2
    "${BASE_DEFINES[@]}"
    "-fuse-ld=lld"
    "${WARN_FLAGS[@]}"
    "${XWIN_INCLUDES[@]}"
    "/I${PROTOBUF_INC}"
    /Iprotobuf
)

# ── C++23 flags: use -Xclang -std=c++23 to force proper C++23 mode ──
# clang-cl's /std:c++23 doesn't set __cplusplus correctly, breaking the MSVC STL.
# Passing -Xclang -std=c++23 goes directly to the compiler frontend.
CXX23_FLAGS=(
    $TARGET /EHsc /MT /O2
    -Xclang -std=c++23
    /D__restrict=
    "${BASE_DEFINES[@]}"
    /DNETHOST_USE_AS_STATIC
    "-fuse-ld=lld"
    "${WARN_FLAGS[@]}"
    "${XWIN_INCLUDES[@]}"
    "${PROJECT_INCLUDES[@]}"
)

# ── C flags (Zydis) ──
C_FLAGS=(
    $TARGET /TC /MT /O2 /DNDEBUG
    "-fuse-ld=lld"
    "-Wno-unused-command-line-argument"
    "${XWIN_INCLUDES[@]}"
    /Ivendor
)

echo "=== Compiling protobuf sources ==="
for f in protobuf/*.pb.cc; do
    name=$(basename "$f" .pb.cc)
    echo "  $name.pb.cc"
    clang-cl "${PROTOBUF_FLAGS[@]}" /c "$f" "/Fo${OBJ}/${name}.pb.obj"
done

echo "=== Compiling vendor sources ==="
echo "  safetyhook.cpp"
clang-cl "${CXX23_FLAGS[@]}" /c vendor/safetyhook.cpp "/Fo${OBJ}/safetyhook.obj"

echo "  Zydis.c"
clang-cl "${C_FLAGS[@]}" /c vendor/Zydis.c "/Fo${OBJ}/Zydis.obj"

echo "=== Compiling Source SDK sources ==="
# Source SDK doesn't need C++23; compile with standard clang-cl /std:c++17
# __restrict mismatch between declaration/definition in bitbuf.h is a hard error.
# Patch: redefine __restrict as empty via forced include (before platform.h sets it).
printf '#pragma clang attribute push(__attribute__((annotate("no_restrict"))), apply_to=any)\n' > /tmp/norestrict.h || true
SDK_FLAGS=(
    $TARGET /EHsc /std:c++17 /MT /O2
    /D__restrict=
    "${BASE_DEFINES[@]}"
    /DNETHOST_USE_AS_STATIC
    "-fuse-ld=lld"
    "${WARN_FLAGS[@]}"
    "${XWIN_INCLUDES[@]}"
    "${PROJECT_INCLUDES[@]}"
)
for f in \
    sourcesdk/entity2/entityidentity.cpp \
    sourcesdk/entity2/entitykeyvalues.cpp \
    sourcesdk/entity2/entitysystem.cpp \
    sourcesdk/tier1/convar.cpp \
    sourcesdk/tier1/keyvalues3.cpp; do
    name=$(basename "$f" .cpp)
    echo "  $name.cpp"
    clang-cl "${SDK_FLAGS[@]}" /c "$f" "/Fo${OBJ}/${name}.obj"
done

echo "=== Compiling deadworks sources ==="
PROJECT_FLAGS=("${CXX23_FLAGS[@]}" "/FI${SRC}/pch.hpp" "/I${SRC}")

for f in \
    ${SRC}/startup.cpp \
    ${SRC}/Hosting/DotNetHost.cpp \
    ${SRC}/Core/Hooks/CoreHooks.cpp \
    ${SRC}/Core/Hooks/CBaseEntity.cpp \
    ${SRC}/Core/Hooks/CCitadelPlayerPawn.cpp \
    ${SRC}/Core/Hooks/CCitadelPlayerController.cpp \
    ${SRC}/Core/Hooks/GameEvents.cpp \
    ${SRC}/Core/Hooks/CServerSideClientBase.cpp \
    ${SRC}/Core/Hooks/PostEventAbstract.cpp \
    ${SRC}/Core/Hooks/NetworkServerService.cpp \
    ${SRC}/Core/Hooks/Source2GameClients.cpp \
    ${SRC}/Core/Hooks/Source2Server.cpp \
    ${SRC}/Core/Hooks/TraceShape.cpp \
    ${SRC}/Core/Hooks/EntityIO.cpp \
    ${SRC}/Core/Hooks/ProcessUsercmds.cpp \
    ${SRC}/Core/Hooks/AbilityThink.cpp \
    ${SRC}/Core/Hooks/AddModifier.cpp \
    ${SRC}/Core/Hooks/BuildGameSessionManifest.cpp \
    ${SRC}/Core/Deadworks.cpp \
    ${SRC}/Core/NativeCallbacks.cpp \
    ${SRC}/Core/NativeAbility.cpp \
    ${SRC}/Core/NativeDamage.cpp \
    ${SRC}/Core/NativeHero.cpp \
    ${SRC}/Core/ManagedCallbacks.cpp \
    ${SRC}/Memory/MemoryDataLoader.cpp \
    ${SRC}/Memory/Scanner.cpp \
    ${SRC}/SDK/Interfaces.cpp \
    ${SRC}/SDK/Schema/Schema.cpp; do
    name=$(basename "$f" .cpp)
    echo "  $name.cpp"
    clang-cl "${PROJECT_FLAGS[@]}" /c "$f" "/Fo${OBJ}/${name}.obj"
done

echo "=== Linking deadworks.exe ==="
lld-link \
    /SUBSYSTEM:CONSOLE \
    /OUT:${OUT}/deadworks.exe \
    "/LIBPATH:${XWIN}/crt/lib/x86_64" \
    "/LIBPATH:${XWIN}/sdk/lib/um/x86_64" \
    "/LIBPATH:${XWIN}/sdk/lib/ucrt/x86_64" \
    "/LIBPATH:${PROTOBUF_LIB}" \
    "/LIBPATH:${NETHOST}" \
    sourcesdk/lib/win64/tier0.lib \
    libprotobuf.lib \
    libnethost.lib \
    advapi32.lib \
    ole32.lib \
    ${OBJ}/*.obj

echo "=== Build complete: ${OUT}/deadworks.exe ==="
ls -la "${OUT}/deadworks.exe"
