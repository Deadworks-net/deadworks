#!/usr/bin/env python3
"""
Runs the spawn survey: spawns every name Spawn could have a function for, alone, on real
dedicated servers, and writes what happened to spawn-survey.json for the generator.

    dotnet run --project managed/DeadworksManaged.GameGen -- --survey-dir WORK
    python tools/spawn-survey/survey.py --server-dir "<server>/game/bin/win64" --work WORK

The server directory must be a Deadworks install built from this checkout, with
SpawnSurvey.dll in managed/plugins. Use a private copy of the game, not the install you play
on: the survey crashes its servers on purpose, dozens of times.

Nothing the survey does should reach your screen. The servers run on a Windows desktop of
their own, which is never shown, so the console window the engine opens and anything else a
server puts up stay out of sight. They run with -no_assert_dialog, so an engine assert is
logged instead of waiting on a dialog, and the plugin ends its own process the moment it
faults, before Windows or the .NET runtime can report the crash with a box. The runner still
watches your desktop for a window that belongs to one of its servers, hides it, and says so
at the end; that count should be zero.

A server that dies or stops making progress is replaced; the plugin picks up where the log
ends. A name counts as crashing only after it has taken down two servers, the second time
as the first thing that server did.
"""

import argparse
import ctypes
import ctypes.wintypes
import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

HANG_SECONDS = 45      # no new log line for this long, mid-spawn or not, and the server is replaced
START_SECONDS = 150    # a fresh server gets this long to load the map and write its first line
MAX_IDLE_RESTARTS = 4  # servers in a row that got nowhere, before a shard gives up


# SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX: a crashing process ends
# without Windows asking anyone about it. Child processes inherit the mode.
QUIET_ERROR_MODE = 0x0001 | 0x0002 | 0x8000

DESKTOP = "deadworks-spawn-survey"

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.CreateDesktopW.restype = ctypes.wintypes.HANDLE
kernel32.CreateProcessW.argtypes = [
    ctypes.wintypes.LPCWSTR, ctypes.wintypes.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, ctypes.wintypes.BOOL,
    ctypes.wintypes.DWORD, ctypes.c_void_p, ctypes.wintypes.LPCWSTR, ctypes.c_void_p, ctypes.c_void_p,
]
kernel32.WaitForSingleObject.argtypes = [ctypes.wintypes.HANDLE, ctypes.wintypes.DWORD]
kernel32.TerminateProcess.argtypes = [ctypes.wintypes.HANDLE, ctypes.wintypes.UINT]
kernel32.CloseHandle.argtypes = [ctypes.wintypes.HANDLE]


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.wintypes.DWORD), ("lpReserved", ctypes.wintypes.LPWSTR), ("lpDesktop", ctypes.wintypes.LPWSTR),
        ("lpTitle", ctypes.wintypes.LPWSTR), ("dwX", ctypes.wintypes.DWORD), ("dwY", ctypes.wintypes.DWORD),
        ("dwXSize", ctypes.wintypes.DWORD), ("dwYSize", ctypes.wintypes.DWORD), ("dwXCountChars", ctypes.wintypes.DWORD),
        ("dwYCountChars", ctypes.wintypes.DWORD), ("dwFillAttribute", ctypes.wintypes.DWORD), ("dwFlags", ctypes.wintypes.DWORD),
        ("wShowWindow", ctypes.wintypes.WORD), ("cbReserved2", ctypes.wintypes.WORD), ("lpReserved2", ctypes.c_void_p),
        ("hStdInput", ctypes.wintypes.HANDLE), ("hStdOutput", ctypes.wintypes.HANDLE), ("hStdError", ctypes.wintypes.HANDLE),
    ]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", ctypes.wintypes.HANDLE), ("hThread", ctypes.wintypes.HANDLE),
                ("dwProcessId", ctypes.wintypes.DWORD), ("dwThreadId", ctypes.wintypes.DWORD)]


class HiddenProcess:
    """
    A process whose windows nobody sees: it is started on a desktop of its own. The engine opens
    its console window itself and ignores a request to start hidden, so hiding the window does
    not work; a window on a desktop that is never switched to is simply not on screen.
    """

    desktop = None

    def __init__(self, command, cwd, env):
        if HiddenProcess.desktop is None:
            HiddenProcess.desktop = user32.CreateDesktopW(DESKTOP, None, None, 0, 0x10000000, None)  # GENERIC_ALL
            if not HiddenProcess.desktop:
                raise ctypes.WinError(ctypes.get_last_error())
        startup = STARTUPINFOW()
        startup.cb = ctypes.sizeof(startup)
        startup.lpDesktop = DESKTOP
        information = PROCESS_INFORMATION()
        block = ctypes.create_unicode_buffer("".join(f"{key}={value}\0" for key, value in sorted(env.items())) + "\0")
        line = ctypes.create_unicode_buffer(subprocess.list2cmdline(command))
        # CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT: its own console, which lands on its own desktop.
        if not kernel32.CreateProcessW(None, line, None, None, False, 0x00000010 | 0x00000400, block, str(cwd),
                                       ctypes.byref(startup), ctypes.byref(information)):
            raise ctypes.WinError(ctypes.get_last_error())
        kernel32.CloseHandle(information.hThread)
        self.handle = information.hProcess
        self.pid = information.dwProcessId

    def poll(self):
        """None while the process runs, like subprocess.Popen.poll."""
        return None if kernel32.WaitForSingleObject(self.handle, 0) == 0x102 else 0   # WAIT_TIMEOUT

    def kill(self):
        kernel32.TerminateProcess(self.handle, 1)
        kernel32.WaitForSingleObject(self.handle, 30000)


class WindowWatcher(threading.Thread):
    """Hides any window one of the survey's servers shows on the user's desktop, and counts them."""

    def __init__(self):
        super().__init__(daemon=True)
        self.pids = set()
        self.seen = []
        self.stopping = threading.Event()

    def run(self):
        callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)

        def visit(window, _):
            if not user32.IsWindowVisible(window):
                return True
            length = user32.GetWindowTextLengthW(window)
            title = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(window, title, length + 1)
            pid = ctypes.wintypes.DWORD()
            user32.GetWindowThreadProcessId(window, ctypes.byref(pid))
            # A crash box is the system's window, not the server's, so it is known by its title.
            if pid.value in self.pids or title.value.lower().startswith("deadworks.exe"):
                user32.ShowWindow(window, 0)
                user32.PostMessageW(window, 0x0010, 0, 0)  # WM_CLOSE
                self.seen.append(title.value or "(untitled)")
            return True

        callback = callback_type(visit)
        while not self.stopping.wait(0.05):
            user32.EnumWindows(callback, 0)


def read_log(path):
    entries = []
    if path.exists():
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.strip():
                try:
                    entries.append(json.loads(line))
                except json.JSONDecodeError:
                    pass  # a line the server died in the middle of writing
    return entries


def open_name(entries):
    """The name the log ends on a 'begin' for, if any: what the server was doing when it stopped."""
    for entry in reversed(entries):
        if entry.get("e") == "begin":
            return entry["name"]
        if entry.get("e") in ("end", "done", "killed"):
            return None
    return None


class Shard:
    def __init__(self, index, names, args):
        self.index = index
        self.args = args
        self.list = args.work / f"shard{index}.tsv"
        self.log = args.work / f"shard{index}.jsonl"
        self.list.write_text("\n".join(names) + "\n", encoding="utf-8")
        self.process = None
        self.started = 0.0
        self.idle_restarts = 0
        self.lines_at_start = 0
        self.finished = False

    def start(self):
        exe = self.args.server_dir / "deadworks.exe"
        env = dict(os.environ, DW_SURVEY_LIST=str(self.list), DW_SURVEY_OUT=str(self.log))
        if self.index == 0:
            env["DW_SURVEY_CONVARS"] = str(self.args.work / "convars.txt")
            env["DW_SURVEY_CONVARS_OUT"] = str(self.args.work / "absent-convars.json")
        command = [
            str(exe), "-dedicated", "-console", "-insecure", "-allow_no_lobby_connect", "-no_assert_dialog",
            "-maxplayers", "4", "+hostport", str(self.args.port + self.index), "+sv_hibernate_when_empty", "0", "+map", self.args.map,
        ]
        self.process = HiddenProcess(command, self.args.server_dir, env)
        self.args.watcher.pids.add(self.process.pid)
        self.started = time.time()
        self.lines_at_start = len(read_log(self.log))

    def stop(self):
        if self.process and self.process.poll() is None:
            self.process.kill()

    def step(self):
        """Looks at the shard once. Returns a line to print, or None."""
        if self.finished:
            return None
        entries = read_log(self.log)
        if any(entry.get("e") == "done" for entry in entries):
            self.stop()
            self.finished = True
            return f"shard {self.index}: done"

        running = self.process is not None and self.process.poll() is None
        last_write = self.log.stat().st_mtime if self.log.exists() else 0
        quiet = time.time() - max(last_write, self.started)
        made_progress = len(entries) > self.lines_at_start
        message = None

        if running and quiet > (HANG_SECONDS if made_progress else START_SECONDS):
            name = open_name(entries)
            self.stop()
            running = False
            if name:
                # Say so in the log: a second "begin" alone would read as a crash.
                with self.log.open("a", encoding="utf-8") as log:
                    log.write(json.dumps({"e": "killed", "name": name}) + "\n")
            message = f"shard {self.index}: no progress for {int(quiet)} s" + (f" on {name}, stopped it" if name else ", stopped it")

        if not running:
            name = open_name(entries)
            if self.process is not None and message is None:
                message = f"shard {self.index}: server exited" + (f" on {name}" if name else "")
            self.idle_restarts = 0 if made_progress or self.process is None else self.idle_restarts + 1
            if self.idle_restarts > MAX_IDLE_RESTARTS:
                self.finished = True
                return f"shard {self.index}: gave up, {MAX_IDLE_RESTARTS} servers in a row got nowhere"
            self.start()
        return message


def summarize(args, names):
    """Each name's verdict: its last 'end', or what its unfinished attempts say."""
    ends, begins, kills, faults = {}, {}, {}, {}
    for log in sorted(args.work.glob("shard*.jsonl")):
        for entry in read_log(log):
            name = entry.get("name")
            if entry["e"] == "begin":
                begins[name] = begins.get(name, 0) + 1
            elif entry["e"] == "end":
                ends[name] = entry
            elif entry["e"] == "killed":
                kills[name] = kills.get(name, 0) + 1
            elif entry["e"] == "fault" and name:
                faults[name] = entry.get("at", "")

    results = {}
    for name in names:
        if name in ends:
            end = ends[name]
            result = {"result": end["result"]}
            if end.get("class"):
                result["class"] = end["class"]
            if end.get("model"):
                result["model"] = end["model"]
            if begins.get(name, 1) > 1:
                result["attempts"] = begins[name]   # lived on a fresh server after dying on a used one
        elif begins.get(name, 0) >= 2:
            result = {"result": "hung" if kills.get(name, 0) >= 2 else "crashed"}
            if faults.get(name):
                result["at"] = faults[name]   # where the server faulted, as module+offset in this build
        else:
            continue   # never got to it, or died once and the survey stopped: not a verdict
        results[name] = result
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--server-dir", type=Path, required=True, help="game/bin/win64 of a private Deadworks server")
    parser.add_argument("--work", type=Path, required=True, help="directory with names.tsv and convars.txt from the generator")
    parser.add_argument("--out", type=Path, default=Path("managed/DeadworksManaged.GameGen/spawn-survey.json"))
    parser.add_argument("--map", default="dl_midtown")
    parser.add_argument("--shards", type=int, default=3, help="servers to run at once")
    parser.add_argument("--port", type=int, default=27080, help="first server's port; each shard takes the next")
    parser.add_argument("--summarize-only", action="store_true", help="write the result from existing logs without running servers")
    args = parser.parse_args()

    names = [line.split("\t")[0] for line in (args.work / "names.tsv").read_text(encoding="utf-8").splitlines() if line.strip()]
    if not args.summarize_only:
        kernel32.SetErrorMode(QUIET_ERROR_MODE)
        watcher = args.watcher = WindowWatcher()
        watcher.start()
        started = time.time()
        shards = [Shard(i, names[i::args.shards], args) for i in range(args.shards)]
        try:
            last_report = 0.0
            while not all(shard.finished for shard in shards):
                for shard in shards:
                    message = shard.step()
                    if message:
                        print(message, flush=True)
                if time.time() - last_report > 60:
                    last_report = time.time()
                    done = sum(1 for log in args.work.glob("shard*.jsonl") for entry in read_log(log) if entry.get("e") == "end")
                    print(f"{done} of {len(names)} names finished", flush=True)
                time.sleep(2)
        finally:
            for shard in shards:
                shard.stop()
            watcher.stopping.set()
            # The engine writes a minidump for each crash; the survey has no use for them.
            for dump in args.server_dir.glob("*.mdmp"):
                if dump.stat().st_mtime >= started:
                    dump.unlink(missing_ok=True)
        print(f"Windows that reached your desktop: {len(watcher.seen)}" + (f" ({', '.join(sorted(set(watcher.seen)))})" if watcher.seen else ""), flush=True)

    results = summarize(args, names)
    inf = (args.server_dir / ".." / ".." / "citadel" / "steam.inf").resolve().read_text(encoding="utf-8", errors="replace")
    build = int(next(line.split("=")[1] for line in inf.splitlines() if line.startswith("ClientVersion=")))
    absent_path = args.work / "absent-convars.json"
    survey = {
        "build": build,
        "map": args.map,
        "results": dict(sorted(results.items())),
        "absentConVars": json.loads(absent_path.read_text(encoding="utf-8")) if absent_path.exists() else [],
    }
    args.out.write_text(json.dumps(survey, indent=1) + "\n", encoding="utf-8")

    counts = {}
    for result in results.values():
        counts[result["result"]] = counts.get(result["result"], 0) + 1
    print(f"Wrote {args.out}: {len(results)} of {len(names)} names; " + ", ".join(f"{count} {kind}" for kind, count in sorted(counts.items())))
    print(f"{len(survey['absentConVars'])} console variables are not registered on a dedicated server")
    missing = [name for name in names if name not in results]
    if missing:
        print(f"No verdict for {len(missing)}: " + ", ".join(missing[:20]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
