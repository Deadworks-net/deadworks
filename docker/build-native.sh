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
# Source SDK uses C++17; __restrict mismatch in bitbuf.h requires /D__restrict=
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

# The vcxproj is the source list; keeping a second list here misses new upstream hooks.
mapfile -t source_files < <(python3 - <<'PY'
import xml.etree.ElementTree as ET
root = ET.parse("deadworks/deadworks.vcxproj").getroot()
for node in root.iter("{http://schemas.microsoft.com/developer/msbuild/2003}ClCompile"):
    path = node.attrib.get("Include", "").replace("\\", "/")
    if path.startswith("src/") and path != "src/pch.cpp":
        print("deadworks/" + path)
PY
)
test ${#source_files[@]} -gt 0
for f in "${source_files[@]}"; do
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
