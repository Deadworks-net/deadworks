#!/usr/bin/env python3
"""
Fetches .gameevents files from SteamTracking/GameTracking-Deadlock and updates
the local game_exported/ directory.
"""

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_URL = "https://github.com/SteamTracking/GameTracking-Deadlock.git"

# Mapping of upstream path -> local path (relative to project root)
FILES = {
    "game/core/pak01_dir/resource/core.gameevents": "game_exported/core.gameevents",
    "game/citadel/pak01_dir/resource/game.gameevents": "game_exported/game.gameevents",
}

# Directories to sparse-checkout from upstream
SPARSE_DIRS = [
    "game/core/pak01_dir/resource",
    "game/citadel/pak01_dir/resource",
]


def get_project_root() -> Path:
    return Path(__file__).resolve().parent.parent


def clone_upstream(dest: Path) -> Path:
    """Shallow-clone the upstream repo with sparse checkout."""
    print(f"Cloning {REPO_URL} (shallow) ...")
    subprocess.run(
        ["git", "clone", "--depth", "1", "--filter=blob:none", "--sparse", REPO_URL, str(dest)],
        check=True,
    )
    subprocess.run(
        ["git", "sparse-checkout", "set", *SPARSE_DIRS],
        cwd=dest,
        check=True,
    )
    return dest


def main() -> None:
    project_root = get_project_root()
    game_exported_dir = project_root / "game_exported"

    if not game_exported_dir.exists():
        print(f"Error: {game_exported_dir} not found. Run from the project root.", file=sys.stderr)
        sys.exit(1)

    tmpdir = Path(tempfile.mkdtemp(prefix="deadworks-gameevents-"))
    try:
        clone_dir = tmpdir / "upstream"
        clone_upstream(clone_dir)

        updated = 0
        for upstream_path, local_path in FILES.items():
            src = clone_dir / upstream_path
            dst = project_root / local_path

            if not src.exists():
                print(f"  Warning: {upstream_path} not found upstream, skipping")
                continue

            shutil.copy2(src, dst)
            print(f"  Updated {local_path}")
            updated += 1

        print(f"\nDone! Updated {updated} file(s). Review changes with: git diff --stat")
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)


if __name__ == "__main__":
    main()
