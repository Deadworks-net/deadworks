#!/usr/bin/env python3
"""
Fetches .proto files from SteamTracking/GameTracking-Deadlock and regenerates
the compiled C++ protobuf files (protobuf/*.pb.cc/h) and managed proto sources
(managed/protos/*.proto).

Requires git and the sourcesdk submodule (for protoc).
"""

import platform
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_URL = "https://github.com/SteamTracking/GameTracking-Deadlock.git"

# Proto files to compile to C++ (.pb.cc/.pb.h)
CPP_PROTOS = [
    "netmessages",
    "network_connection",
    "networkbasetypes",
    "networksystem_protomessages",
    "source2_steam_stats",
]

# Additional proto files to copy for managed/protos/ (beyond CPP_PROTOS)
EXTRA_MANAGED_PROTOS = [
    "citadel_gameevents",
    "citadel_usercmd",
    "te",
    "usercmd",
    "usermessages",
]


def get_project_root() -> Path:
    return Path(__file__).resolve().parent.parent


def find_protoc(project_root: Path) -> tuple[Path, Path]:
    """Find protoc and its include dir from the sourcesdk submodule."""
    system = platform.system()
    if system == "Windows":
        protoc = project_root / "sourcesdk" / "devtools" / "bin" / "win64" / "protoc.exe"
    elif system == "Darwin":
        protoc = project_root / "sourcesdk" / "devtools" / "bin" / "osx64" / "protoc"
    else:
        protoc = project_root / "sourcesdk" / "devtools" / "bin" / "linuxsteamrt64" / "protoc"

    include_dir = project_root / "sourcesdk" / "thirdparty" / "protobuf" / "src"

    if not protoc.exists() or not include_dir.exists():
        print("Error: sourcesdk submodule not initialized. Run: git submodule update --init", file=sys.stderr)
        sys.exit(1)

    return protoc, include_dir


def clone_upstream(dest: Path) -> Path:
    """Shallow-clone the upstream repo and return the Protobufs directory."""
    print(f"Cloning {REPO_URL} (shallow) ...")
    subprocess.run(
        ["git", "clone", "--depth", "1", "--filter=blob:none", "--sparse", REPO_URL, str(dest)],
        check=True,
    )
    subprocess.run(
        ["git", "sparse-checkout", "set", "Protobufs"],
        cwd=dest,
        check=True,
    )
    return dest / "Protobufs"


def copy_managed_protos(upstream_dir: Path, managed_dir: Path) -> None:
    """Copy .proto files to managed/protos/, updating only files that already exist."""
    copied = 0
    for existing in sorted(managed_dir.glob("*.proto")):
        src = upstream_dir / existing.name
        if src.exists():
            shutil.copy2(src, existing)
            copied += 1
        else:
            print(f"  Warning: {existing.name} not found upstream, skipping")
    print(f"Copied {copied} .proto files to {managed_dir}")


def compile_cpp_protos(
    protoc: Path, protobuf_include: Path, upstream_dir: Path, output_dir: Path
) -> None:
    """Compile .proto files to C++ using protoc."""
    print(f"Compiling {len(CPP_PROTOS)} proto files with {protoc} ...")
    for name in CPP_PROTOS:
        proto_file = f"{name}.proto"
        src = upstream_dir / proto_file
        if not src.exists():
            print(f"  Warning: {proto_file} not found upstream, skipping")
            continue

        result = subprocess.run(
            [
                str(protoc),
                f"-I{protobuf_include}",
                f"-I{upstream_dir}",
                f"--cpp_out={output_dir}",
                proto_file,
            ],
            cwd=upstream_dir,
            capture_output=True,
            text=True,
        )
        if result.returncode != 0:
            print(f"  Error compiling {proto_file}:\n{result.stderr}", file=sys.stderr)
            sys.exit(1)
        print(f"  Compiled {proto_file}")

    print(f"Generated C++ files in {output_dir}")


def main() -> None:
    project_root = get_project_root()
    protobuf_dir = project_root / "protobuf"
    managed_protos_dir = project_root / "managed" / "protos"

    if not protobuf_dir.exists():
        print(f"Error: {protobuf_dir} not found. Run from the project root.", file=sys.stderr)
        sys.exit(1)

    protoc, protobuf_include = find_protoc(project_root)
    print(f"Using protoc: {protoc}")

    tmpdir = Path(tempfile.mkdtemp(prefix="deadworks-protos-"))
    try:
        # Clone upstream
        clone_dir = tmpdir / "upstream"
        upstream_protos = clone_upstream(clone_dir)

        # Update managed protos
        if managed_protos_dir.exists():
            copy_managed_protos(upstream_protos, managed_protos_dir)
        else:
            print(f"Warning: {managed_protos_dir} not found, skipping managed protos")

        # Compile C++ protos
        compile_cpp_protos(protoc, protobuf_include, upstream_protos, protobuf_dir)

        print("\nDone! Review changes with: git diff --stat")
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)


if __name__ == "__main__":
    main()
