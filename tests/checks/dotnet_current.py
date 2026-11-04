"""Fails when the .NET runtime Pulse ships has fallen behind Microsoft's newest .NET 10 release.

Pulse pins its runtime (RuntimeFrameworkVersion in Pulse.csproj) so a release cannot quietly ship
an old one, as it once did, two security patches behind. Pinning has the opposite risk: nothing
moves it on. Dependabot does not watch this pin, so this does, from Microsoft's own release list.
GitHub runs it every week and on every change to the project file, and a failure emails the owner.

    python tests/checks/dotnet_current.py

Exits 0 when current, 1 when behind, 2 when the release list cannot be read (which says so rather
than passing: a check that silently did not run is no check).
"""
import json
import re
import sys
import urllib.request
from pathlib import Path

RELEASES = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json"
REPO = Path(__file__).resolve().parents[2]


def version(text):
    """(10, 0, 14) from "10.0.14"; None for a preview ("10.0.0-rc.2"), which Pulse never ships."""
    return tuple(int(part) for part in text.split(".")) if re.fullmatch(r"\d+\.\d+\.\d+", text or "") else None


def main():
    project = (REPO / "Pulse.csproj").read_text(encoding="utf-8-sig")
    pinned = re.search(r"<RuntimeFrameworkVersion>([\d.]+)</RuntimeFrameworkVersion>", project)
    if not pinned:
        print("FAIL  Pulse.csproj does not pin RuntimeFrameworkVersion")
        return 1
    ours = pinned.group(1)

    try:
        with urllib.request.urlopen(RELEASES, timeout=30) as reply:
            data = json.load(reply)
    except Exception as error:  # the network, or a reply that is not the list
        print(f"FAIL  could not read Microsoft's release list: {error}")
        return 2

    latest = data.get("latest-runtime", "")
    if not re.fullmatch(r"\d+\.\d+\.\d+", latest):
        print(f"FAIL  Microsoft's release list gave no usable latest runtime: {latest!r}")
        return 2

    if version(ours) >= version(latest):
        print(f"PASS  Pulse ships .NET {ours}, Microsoft's newest is {latest}")
        return 0

    # Which of the newer releases fixed security problems: what makes this worth acting on.
    newer = [r for r in data.get("releases", [])
             if version(r.get("release-version", "")) and version(r["release-version"]) > version(ours)]
    fixes = sorted({c.get("cve-id", "") for r in newer for c in r.get("cve-list", []) if c.get("cve-id")})
    print(f"FAIL  Pulse ships .NET {ours}, Microsoft's newest is {latest} ({data.get('latest-release-date', 'date unknown')})")
    if fixes:
        more = f" and {len(fixes) - 5} more" if len(fixes) > 5 else ""
        print(f"      {len(fixes)} security fixes since {ours}: {', '.join(fixes[:5])}{more}")
    print(f"      move RuntimeFrameworkVersion in Pulse.csproj to {latest}, and the SDK in the workflows "
          f"to {data.get('latest-sdk', 'the newest')}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
