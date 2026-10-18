# Proves the stability suite has teeth: each fault below puts one 1.3.2 fix back the way it
# was, in a scratch copy of the source, and the named check must then fail.
#
#     python teeth.py
#
# Nothing here touches the real source. The copy lives in the system's temporary folder.

import shutil, subprocess, sys, tempfile
from pathlib import Path

HERE  = Path(__file__).resolve().parent
REPO  = HERE.parents[2]
# Outside the repository, so a scratch copy can never be committed by accident.
COPY  = Path(tempfile.gettempdir()) / "pulse-stability-teeth"
TEST  = HERE / "bin" / "Release" / "net10.0-windows"

FAULTS = [
    ("a sleeping card's old readings pass through",
     "Services/SensorSnapshot.cs",
     "            d.GpuTemp = d.GpuPower = d.GpuClock = d.GpuUsage = null;",
     "",
     "no temperature from before it slept"),
    # Not "asleep is never reported": that cannot be told apart from a card that is simply awake
    # at the desk, and the battery test is what proves it. This is the dangerous direction.
    ("an unknown card treated as asleep",
     "Services/GpuPower.cs",
     "            return matches == 1 ? answer : null;",
     "            return matches == 1 ? answer : true;",
     "an adapter that does not exist is unknown, not asleep"),
    ("settings guard removed",
     "Services/SettingsService.cs",
     "bool saved = Settings.ReadFailed ? RefuseToOverwrite() : Settings.Save();",
     "bool saved = Settings.Save();",
     "saving still refuses once the file is readable again"),
    ("corner display back to index only",
     "Views/OverlayWindow.xaml.cs",
     "if (!string.IsNullOrEmpty(savedBounds))",
     "if (false)",
     "the saved display, after Windows swaps the order"),
    ("patience never grows",
     "Services/HardwareService.cs",
     "var stretched = allowance * (1 + Math.Max(0, silentReplacements));",
     "var stretched = allowance;",
     "one silent replacement: twice as long"),
    ("probe maps as an image resource again",
     "Services/NativeLibraryProbe.cs",
     "private const uint DontResolveDllReferences = 0x00000001;",
     "private const uint DontResolveDllReferences = 0x00000020;",
     "and the code mapping is what catches it"),
    ("unreadable treated as missing",
     "Models/AppSettings.cs",
     "return (null, ReadState.Unreadable);",
     "return (null, ReadState.Missing);",
     "a file held open with no sharing is Unreadable"),
]

def run(cmd, cwd):
    return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)

ok = bad = 0
for name, rel, find, replace, check in FAULTS:
    shutil.rmtree(COPY, ignore_errors=True)
    shutil.copytree(REPO, COPY, ignore=shutil.ignore_patterns("bin", "obj", "installer", "publish", ".git", ".vs"))
    f = COPY / rel
    text = f.read_text(encoding="utf-8-sig")
    if text.count(find) != 1:
        print(f"  BROKEN  {name}: fault point not found"); bad += 1; continue
    f.write_text(text.replace(find, replace), encoding="utf-8-sig")

    r = run(["dotnet", "build", "-c", "Debug", "--nologo", "-v", "q"], COPY)
    if r.returncode != 0:
        print(f"  BROKEN  {name}: the faulty copy did not build"); print(r.stdout[-1500:]); bad += 1; continue

    for dll in (COPY / "bin" / "Debug" / "net10.0-windows").glob("*.dll"):
        shutil.copy2(dll, TEST / dll.name)

    out = run([str(TEST / "StabilityTest.exe")], HERE).stdout
    caught = any(line.strip().startswith("FAIL") and check in line for line in out.splitlines())
    print(f"  {'PASS' if caught else 'FAIL'}  {name}: {'caught by' if caught else 'NOT caught by'} '{check}'")
    if caught: ok += 1
    else: bad += 1

# Put the real build back next to the test.
for dll in (REPO / "bin" / "Debug" / "net10.0-windows").glob("*.dll"):
    shutil.copy2(dll, TEST / dll.name)

print(f"\n{ok} guards have teeth, {bad} do not")
sys.exit(0 if bad == 0 else 1)
