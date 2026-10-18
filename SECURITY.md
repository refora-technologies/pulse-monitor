# Security

Pulse runs as administrator, can install a kernel driver, and updates itself. Those are the
three things that make its security matter more than most small apps', so they are what this
policy is about.

## Reporting a problem

If you find a way to misuse Pulse, please tell us **privately**: use
[Report a vulnerability](https://github.com/refora-technologies/pulse-monitor/security/advisories/new)
on this repository's Security tab. Only you and Refora Technologies can see the report, and we
can work on the fix with you there. If you would rather not use GitHub, email
**reforatech@gmail.com** with "Pulse security" in the subject. Please don't open a public issue
or discussion about it until it is fixed.

It helps to include:

- what an attacker could do, and what they would need first (an ordinary account on the same
  PC, a file in a particular place, a position on the network, and so on)
- the steps to reproduce it, or a proof of concept
- the Pulse version (in the control panel, under About) and your Windows version

## What happens next

- We reply within three working days to say we have it.
- We confirm the problem, work on a fix, and keep you told how it is going.
- The fix goes out in a release, and the release notes describe the problem once people have
  had the chance to update.
- With your permission, we thank you by name in those notes.

Please give us a reasonable time to fix it before you talk about it publicly, and don't
access other people's data or disrupt other people's computers while testing.

## Supported versions

Fixes go into the newest release. Pulse checks for updates every day and installs them when
you agree, so the newest version is the one to test and the one we support.

| Version | Supported |
|---|---|
| Latest release | Yes |
| Anything older | No: please update |

## In scope

- **Pulse running as administrator:** anything that lets another program or user make Pulse
  do something on their behalf, load code it shouldn't, or write where it shouldn't.
- **The installer and uninstaller**, including the optional sensor driver they install and
  remove.
- **Updates:** Pulse checks a SHA-256 checksum before running a downloaded installer and
  refuses one that doesn't match. A way around that check, or around where the download is
  kept, is in scope.
- **The files Pulse writes:** its settings, its logs, and the diagnostics file you can save.

Problems in the components Pulse is built on (the PawnIO driver, LibreHardwareMonitor,
PresentMon, .NET) are welcome too. We will pass them on to their makers.
