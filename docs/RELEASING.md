# Releasing Pulse

Every release follows these steps, in this order. Nothing is published by a robot: GitHub
builds and drafts, a person tests the drafted installer on real hardware, and only then
publishes.

## 1. The work is on main, and the checks are green

Each change lands as its own commit with a one-line message, and the **Checks** workflow
passes on GitHub: repository hygiene, the source checks and their fault tests, and every suite
in `tests/`.

## 2. Hardware tests, before the version changes

The checks on GitHub cannot reach a real sensor driver, a laptop's graphics card or an
installer running as administrator. Before a release those are tested on real hardware:

- every test suite, run as administrator with the sensor driver installed
- a real upgrade from the previous release's installer, and the install checks after it
- for anything touching sensors or graphics, the card's behaviour in real time: under load,
  back at idle, and asleep

## 3. Version and notes

- The version goes up in `Pulse.csproj` and in `setup.iss` (AppVersion, AppVerName,
  VersionInfoVersion, VersionInfoProductVersion), in one commit: "Bump to X.Y.Z".
- The notes go in `release-notes/X.Y.Z.md`, written for the person using Pulse:
  - only what they can see, feel or act on, compared with the last release
  - sections **Fixed** and **Improved**, no opening summary line
  - each line says briefly what was wrong and what it is now, in plain English
  - nothing claimed as fixed that has not been confirmed

## 4. Tag, and GitHub drafts the release

```
git tag vX.Y.Z
git push origin main vX.Y.Z
```

The **Release build** workflow checks the tag, the versions and the notes agree, runs the
checks again with these notes, builds the installer from pinned tools and verified third-party
files, attests where it was built, and creates a **draft** release with `PulseSetup.exe` and
`PulseSetup.exe.sha256`. Drafts are invisible to Pulse's updater.

## 5. Test the drafted installer

Download the draft's `PulseSetup.exe` and test that exact file on real hardware: the upgrade
from the previous release, the install checks, and Pulse running afterwards. If anything is
wrong, delete the draft and the tag, fix it, and go back to step 1.

## 6. Publish

Publish the draft on GitHub. From that moment Pulse's updater offers it.

Then update the website's fallback version and installer size, and deploy it.
