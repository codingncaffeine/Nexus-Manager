# Nexus Manager

A sensor display for the Corsair iCUE NEXUS on Linux.

The NEXUS is a 640×48 touchscreen that clips to a keyboard. Corsair ships it
with iCUE, which is Windows-only — and Corsair's own SDK is a *client of the
iCUE daemon* rather than a hardware interface, so it cannot help on Linux
either. This talks to the panel directly over USB HID.

Not affiliated with or endorsed by Corsair.

## What it does

- Renders configurable sensor readouts to the panel at 24 fps
- Discovers every sensor the machine exposes — on a typical desktop that is
  70+ across CPU, GPU, drives, memory, network and the motherboard
- Scrolling strip charts with session low/high beside each reading
- Multiple screens, changed by swiping the panel
- Celsius, Fahrenheit or Kelvin
- A GUI editor with a live preview that pushes to the panel as you edit

## Requirements

- .NET 10
- A udev rule granting access to the device

## Install the udev rule

```sh
sudo cp packaging/70-icue-nexus.rules /etc/udev/rules.d/
sudo udevadm control -R && sudo udevadm trigger --action=add --subsystem-match=hidraw
```

The rule **must** sort below 73. `73-seat-late.rules` is what turns
`TAG=="uaccess"` into an ACL, and udev evaluates rule files in lexical order —
a rule numbered 99 applies the tag *after* the consumer has run, so it grants
nothing while looking entirely correct.

## Build and run

```sh
dotnet build -c Release
./packaging/install-desktop.sh          # desktop entry, icons, PATH symlinks

nexus-manager sensors                   # list everything discovered
nexus-manager init                      # write a starter config
nexus-manager daemon                    # drive the panel
nexus-manager-editor                    # GUI editor
```

Configuration lives at `$XDG_CONFIG_HOME/nexus-manager/screens.json`
(`~/.config/nexus-manager/screens.json`).

## Run it at login

```sh
mkdir -p ~/.config/systemd/user
cp packaging/nexus-manager.service ~/.config/systemd/user/
systemctl --user enable --now nexus-manager
```

## Documentation

- [`docs/PROTOCOL.md`](docs/PROTOCOL.md) — the USB HID protocol, measured
  against real hardware: reports, pixel format, bit depth, touch, throughput
- [`docs/SENSORS.md`](docs/SENSORS.md) — how sensors are discovered and named
- [`docs/CUESCREENS-FORMAT.md`](docs/CUESCREENS-FORMAT.md) — the `.cuescreens`
  container format

## Prior work

The protocol was first documented by others, and this would have been far
slower without them:

- [willneedit/NexusTool](https://github.com/willneedit/NexusTool)
- [fhuber83/ICueNexusPlusPlus](https://github.com/fhuber83/ICueNexusPlusPlus)
- [bitfocus/companion-module-icue-nexus](https://github.com/bitfocus/companion-module-icue-nexus)
- [aluferraz/inexus-osx](https://github.com/aluferraz/inexus-osx)
