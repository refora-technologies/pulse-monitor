# Keeps the repository to finished work. Experiments, measurements, traces, logs and anything
# from one person's machine belong in a private lab, never here; this fails the build if any of
# it is committed.
#
#     python tests/checks/hygiene.py              checks every tracked file
#     python tests/checks/hygiene.py --self-test  proves each rule catches what it is for
#
# The rules are general on purpose. Listing anyone's name or account here would publish it.

import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

# Traces, dumps and logs: measurement output, never source.
BANNED_SUFFIXES = (".etl", ".dmp", ".trace", ".evtx", ".log", ".blg")
BANNED_NAMES = re.compile(r"(^|/)pulse-diagnostics-[^/]*\.txt$", re.IGNORECASE)

# Folder names used for lab work.
BANNED_PARTS = ("_tests/", ".build/", "set-aside/", "gpu-idle/", "gpu-wake/", "battery-test/",
                "tile-availability-ui/", "scenarios-", "poketrace-")

# A path inside someone's Windows profile, or on a drive other than C:, is a path from one
# machine. %USERPROFILE% and environment variables are fine; they are what code should use.
PERSONAL_PATH = re.compile(r"\b[A-Za-z]:[\\/]+(?:Users|Documents and Settings)[\\/]+(?![%$])\w"
                           r"|\b[D-Zd-z]:[\\/]+\w")

# Anything bigger than this needs a reason to be in the repository.
MAX_BYTES = 1_000_000
ALLOWED_LARGE = set()

TEXT_SUFFIXES = (".cs", ".xaml", ".py", ".ps1", ".iss", ".csproj", ".md", ".txt", ".yml",
                 ".yaml", ".json", ".manifest", ".gitignore", ".sh", ".cmd", ".bat")


def problems(files, read):
    """Every rule broken by these files. `read(path)` returns its text, or None."""
    found = []
    for path in files:
        p = path.replace("\\", "/")
        low = p.lower()
        if low.endswith(BANNED_SUFFIXES) or BANNED_NAMES.search(p):
            found.append(("measurement or log file", p))
        for part in BANNED_PARTS:
            if ("/" + low).find("/" + part) >= 0:
                found.append(("lab folder", p))
                break
        size = read(p, size=True)
        if size is not None and size > MAX_BYTES and p not in ALLOWED_LARGE:
            found.append((f"file over {MAX_BYTES:,} bytes", p))
        # This file is exempt from the path rule only: its self-test holds made-up example paths.
        if p == "tests/checks/hygiene.py":
            continue
        if low.endswith(TEXT_SUFFIXES) or "." not in Path(p).name:
            text = read(p)
            if text:
                for n, line in enumerate(text.splitlines(), 1):
                    if PERSONAL_PATH.search(line):
                        found.append(("path from one machine", f"{p}:{n}"))
    return found


def read_repo(path, size=False):
    f = REPO / path
    if not f.is_file():
        return None
    if size:
        return f.stat().st_size
    try:
        return f.read_text(encoding="utf-8-sig", errors="replace")
    except OSError:
        return None


def self_test():
    """Feeds made-up files through the rules; each must be caught, and clean ones must not."""
    samples = {
        "trace-r1.etl": "",
        "notes/pulse-diagnostics-20261018-101010.txt": "",
        "_tests/run-all.py": "",
        "docs/.build/x.md": "",
        "Services/Thing.cs": 'var p = @"C:\\Users\\someone\\AppData\\Local";',
        "build.ps1": "Copy-Item D:\\work\\pulse\\x.exe .",
        "big.bin": None,
    }
    clean = {
        "Services/Fine.cs": 'var p = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);',
        "setup.iss": 'Source: "C:\\Program Files\\x"; DestDir: "{app}"',
        "README.md": "Paths are shown as %USERPROFILE%\\AppData\\Roaming\\Refora\\Pulse.",
    }

    def fake(path, size=False):
        if size:
            return 5_000_000 if path == "big.bin" else 10
        return samples.get(path, clean.get(path))

    ok = True
    caught = {p.split(":")[0] for _, p in problems(list(samples), fake)}
    for name in samples:
        hit = name in caught
        ok &= hit
        print(f"  {'PASS' if hit else 'FAIL'}  caught: {name}")
    for name in clean:
        hit = bool(problems([name], fake))
        ok &= not hit
        print(f"  {'PASS' if not hit else 'FAIL'}  left alone: {name}")
    total = len(samples) + len(clean)
    failed = sum(1 for n in samples if n not in caught) + sum(1 for n in clean if problems([n], fake))
    print(f"\n{total - failed} passed, {failed} failed")
    return 0 if ok else 1


def main():
    if "--self-test" in sys.argv:
        return self_test()
    files = subprocess.run(["git", "ls-files"], cwd=REPO, capture_output=True, text=True,
                           check=True).stdout.splitlines()
    found = problems(files, read_repo)
    print(f"Checked {len(files)} tracked files.")
    for rule, where in found:
        print(f"  FAIL  {rule}: {where}")
    print(f"\n{0 if found else 1} passed, {1 if found else 0} failed")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
