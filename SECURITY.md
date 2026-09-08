# Security

Nexus Manager talks to a USB HID device, can synthesise keystrokes, and can run
commands you configure. This document says plainly what that means, what the
application does *not* need, and what was found and fixed when the code was
reviewed for it.

## Reporting something

Open a [security advisory](https://github.com/codingncaffeine/Nexus-Manager/security/advisories/new)
rather than a public issue. If that is not available to you, open an issue
saying only that you have a report and how to reach you.

## What it does not need

- **No root.** Nothing in the application runs privileged. The panel is reached
  through a udev ACL granted to the logged-in user, not by escalating.
- **No setuid, no capabilities, no polkit action.**
- **No network.** It makes no outbound connections, has no update checker, and
  sends no telemetry. The bundled systemd unit sets `IPAddressDeny=any` and
  `PrivateNetwork=yes` so that this is enforced rather than merely true.
- **No system daemon.** The service is a *user* unit. There is nothing running
  as root at any point after installation.

## Device access

The udev rule tags the panel with `uaccess`:

```
KERNEL=="hidraw*", ATTRS{idVendor}=="1b1c", ATTRS{idProduct}=="1b8e", TAG+="uaccess"
```

`uaccess` grants an ACL to **the active local seat's user only**, and revokes it
at logout. It is deliberately not `MODE="0666"`, which would hand the device to
every process on the machine, permanently.

## The trust boundary: your configuration is code

`screens.json` can contain a `Launch` action, and a `Launch` action runs its
target through `/bin/sh -c`. **Anyone who can write your configuration file can
run commands as you.**

This is intentional — it is what makes a touch button useful — and it is the
same trust level as `~/.bashrc` or a `.desktop` file in `~/.local/share`. It is
worth stating explicitly because it means:

- Do not run a `screens.json` you did not write or read.
- The configuration lives under `$XDG_CONFIG_HOME/nexus-manager/`, protected by
  ordinary file permissions. There is no separate sandbox for it.
- `.cuescreens` import is **not implemented**. If it ever is, imported actions
  will need to be neutered or confirmed, because that would be the first path
  by which a file from someone else could carry an action.

Every other action type is built from fixed argument arrays and passed with
`ArgumentList`, never through a shell, so a configured value cannot inject
arguments or commands.

## Synthetic keystrokes

A touch button can press a key, which is done by creating a virtual keyboard on
`/dev/uinput`. On Wayland there is no XTEST, so there is no other way to do it;
`ckb-next` solves the same problem the same way.

The consequence worth understanding: **while the application is running it can
type into whatever has focus.** Key names are resolved through a fixed
allow-list of key codes, so a configuration cannot inject an arbitrary scan
code — but it can certainly ask for a real key.

Access to `/dev/uinput` is by ACL, from the same `uaccess` mechanism as the
panel. If your distribution does not grant it, key actions fail and everything
else keeps working.

## Single-instance lock and the local socket

One process owns the panel at a time. Two writers to the same `hidraw` node do
not error — the reports interleave and the display flickers — so ownership is
arbitrated with an exclusive lock file in `$XDG_RUNTIME_DIR`, which is mode 0700
and per-user.

The running instance listens on a Unix socket in the same directory. It accepts
exactly one message, `show`, which raises the window. Nothing else is parsed and
nothing else is actioned.

If `XDG_RUNTIME_DIR` is unset, the fallback under `/tmp` is created mode 0700 and
**its mode is checked before use**; a directory that is not exactly 0700 is
refused rather than trusted, because `/tmp` is world-writable and that path is
predictable.

## Sandboxing

`packaging/nexus-manager.service` scores **1.3 OK** under
`systemd-analyze security` (it was 7.0 MEDIUM before this pass). Verify it
yourself:

```sh
systemd-analyze security --user nexus-manager.service
```

Deliberately **not** enabled, with reasons:

| Directive | Why not |
|---|---|
| `MemoryDenyWriteExecute=true` | The .NET runtime JITs. This stops the service starting at all. |
| `SystemCallFilter=~@resources` | Same — the runtime needs syscalls in that set and dies with `Failed to create CoreCLR`. `@system-service` alone is applied. |
| `RestrictAddressFamilies=AF_UNIX` alone | Device enumeration goes through udev, which needs `AF_NETLINK`. Both are allowed and nothing else is. |

⛔ A hardening score measures the policy, not whether the program still runs
under it. Every directive above was applied and then the service was started and
checked for its ready state. Two of them had to be removed because the score
improved while the daemon would not boot.

## Found and fixed in this review

- **A corrupt `screens.json` crashed the application with a core dump.** A
  truncated write left it unstartable with no message. It now reports the parse
  error, moves the unreadable file aside instead of overwriting it, and starts
  from a discovered configuration.
- **The `/tmp` fallback directory was created with default permissions.** On a
  multi-user machine another user could create the predictable path first and
  then own the directory the lock, owner and socket files are made in — and
  `File.WriteAllText` follows symlinks. Now created 0700 and mode-checked.
- **Lock failures were reported as "already running".** A permissions fault, and
  later a sandbox denial, both surfaced as a message telling the user to stop a
  process that did not exist. Configuration faults are now separated from
  contention and reported as themselves.
- **The bundled systemd unit could never start the daemon.** `ProtectHome=` also
  covers `/run/user`, so the lock file's directory was read-only; the failure
  then presented as the bogus "already running" above. Fixed with
  `ReadWritePaths=%t`.
- **A launched program could deadlock or pin a thread.** The launcher read one
  pipe to the end before the other, so a child filling stderr hung both, and
  because the read only returned when the child exited, launching a long-lived
  program held a thread for its lifetime. The launcher no longer redirects, and
  the one place that does capture output drains both pipes concurrently under a
  timeout.

## Supply chain

Release packages bundle the .NET runtime, so they depend only on system
libraries. The dependency lists in the `.deb` and the PKGBUILD are derived from
a sweep of the published binaries — `objdump -p` for `NEEDED` entries plus a
strings sweep for libraries loaded via `dlopen` — rather than being written from
memory. `packaging/test-deb.sh` asserts the package contents against the build
and then launches the binaries out of the extracted package.

Release artifacts are built from the tagged commit and their checksums are
recorded in the AUR package.
