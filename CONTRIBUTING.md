# Contributing to Pulse

Thank you for wanting to help. Pulse is made by Refora Technologies and is open to everyone's
help: fixes, testing on hardware we don't have, better wording, and ideas.

## Ways to help

- **Report a bug** as an [issue](https://github.com/refora-technologies/pulse-monitor/issues/new/choose),
  with the form filled in. A diagnostics file (control panel, About, Save diagnostics) is the
  single most useful thing you can attach.
- **Ask a question** in [Q&A](https://github.com/refora-technologies/pulse-monitor/discussions/categories/q-a).
- **Suggest an idea** in [Ideas](https://github.com/refora-technologies/pulse-monitor/discussions/categories/ideas).
- **Test on your hardware.** Pulse reads sensors by name, and every CPU and graphics card names
  them a little differently. A diagnostics file from a machine we haven't seen is valuable even
  when everything works.
- **Send a change** as a pull request, as below.

Security problems are the exception: report them privately, as [SECURITY.md](SECURITY.md)
explains.

## Before a bigger change

For anything larger than a fix, such as a new tile, a new setting, or a change to how
something looks or works, start a discussion in Ideas first. Pulse stays deliberately simple,
and it's better to agree on the shape of a change before you spend time on it.

## Building

You need Windows 10 or 11 (x64), the .NET 10 SDK, and Inno Setup 6.

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This publishes Pulse, downloads the two components it bundles (the PawnIO driver installer and
PresentMon) from their official releases, checks each one's SHA-256 against the value pinned in
the script, and builds `installer\PulseSetup.exe` with its checksum file.

Pulse runs as administrator, so running the built `Pulse.exe` shows a UAC prompt.

## Testing

```
powershell -ExecutionPolicy Bypass -File tests\run.ps1
```

These checks run on every push and pull request on GitHub, and need nothing but the SDK. A
change should keep them all passing. A fix should come with a check that would have failed
before it, and a new check should be shown to fail when the thing it guards is broken.

Some behaviour can only be tested on real hardware, as administrator, with the sensor driver
installed. Refora Technologies runs those tests before every release.

## What goes in the repository

Only finished work: the app, its build and installer scripts, the tests in `tests/`, and the
documents. Experiments, measurements, traces, logs, diagnostics files and anything containing
a personal path or machine detail stay out. A check on every push enforces this.

## Commits and pull requests

- One change per commit, with a one-line message that says what changed for the person using
  Pulse, in plain words: "Keep the graphics tiles steady when a laptop card dips into sleep".
- Comments in the code explain why, especially when the obvious approach was tried and failed.
- Anything a person reads in Pulse is plain English, short, and true. Pulse never shows an
  estimate as if it were a reading.

## Contributor terms

By sending a pull request, you agree that your contribution is published under the
[GNU General Public License v3.0](LICENSE), the same licence as the rest of Pulse.
