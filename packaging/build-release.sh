#!/usr/bin/env bash
# Builds the Linux release artifacts for one architecture:
#
#   nexus-manager-<ver>-linux-x64.tar.gz   self-contained; extract anywhere and run ./nexus-manager
#   nexus-manager.deb                      system install under /usr/lib/nexus-manager
#
#   bash packaging/build-release.sh          # x64, the default and the one the AUR reads
#   bash packaging/build-release.sh arm64
#
# The tarball's name is a contract: packaging/aur/PKGBUILD repackages it straight from the
# GitHub release, so its name and internal layout change together with that file.
#
# Always follow this with packaging/test-deb.sh. A build that succeeds and a package that
# installs are both perfectly consistent with an application that cannot start - that has
# happened on a sibling project, where an allow-list copy shipped 0 of 218 assemblies and
# every step still reported success.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oP '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
OUT=packaging/out
ARCH="${1:-x64}"

case "$ARCH" in
    x64)   DEBARCH=amd64; SUFFIX="" ;;
    arm64) DEBARCH=arm64; SUFFIX="-arm64" ;;
    *) echo "Unknown architecture '$ARCH' - expected x64 or arm64." >&2; exit 1 ;;
esac

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
command -v "$DOTNET" >/dev/null 2>&1 || DOTNET=dotnet

rm -rf "$OUT"
PUB="$OUT/publish"
mkdir -p "$PUB"

echo "== publish v$VER (self-contained linux-$ARCH, Release)"
# BOTH executables into ONE directory. They share the entire .NET runtime, so publishing
# them separately would ship it twice - about 70 MB of exact duplicates - and leave two
# trees to keep in step.
"$DOTNET" publish src/NexusManager.Cli/NexusManager.Cli.csproj \
    -c Release -r "linux-$ARCH" --self-contained true -o "$PUB" -v q
"$DOTNET" publish src/NexusManager.Editor/NexusManager.Editor.csproj \
    -c Release -r "linux-$ARCH" --self-contained true -o "$PUB" -v q

# The runtime's LTTng tracing shim is dlopened only when tracing is switched on, and it
# wants liblttng-ust, which nothing else here needs. Tracing is not a feature of this
# application; the bar for the dependency list is that nothing in the package asks for
# something the package did not bring.
rm -f "$PUB/libcoreclrtraceptprovider.so"

# What a person unpacking the tarball should find first.
cp LICENSE "$PUB/LICENSE"
cp README.md "$PUB/README.md"
mkdir -p "$PUB/packaging"
cp packaging/70-icue-nexus.rules packaging/nexus-manager.service \
   packaging/nexus-manager.desktop packaging/nexus-manager-tray.desktop \
   packaging/install-desktop.sh "$PUB/packaging/"
cp -a packaging/icons "$PUB/packaging/"

echo "== tarball"
# ⛔ A reproducible PUBLISH does not give a reproducible TARBALL. tar records
# mtimes, owners and directory order, and gzip stamps the time into its own
# header - so two identical trees still produced two different checksums, and
# nobody could verify that a published artifact came from the published source.
# Pinned: sorted entries, epoch mtimes, numeric root ownership, gzip -n.
# SOURCE_DATE_EPOCH is honoured if set, so a rebuild can match an old release.
TAR_MTIME="@${SOURCE_DATE_EPOCH:-0}"
tar -C "$PUB" --sort=name --mtime="$TAR_MTIME" \
    --owner=0 --group=0 --numeric-owner \
    --pax-option=exthdr.name=%d/PaxHeaders/%f,delete=atime,delete=ctime \
    -cf - . | gzip -n > "$OUT/nexus-manager-$VER-linux-$ARCH.tar.gz"

# ---- the tree the .deb and the AUR package both install --------------------------------
#
# /usr/lib/nexus-manager holds a self-contained bundle, so it gains nothing from the
# lib/lib64 split some distributions make.
ROOT="$OUT/root"
rm -rf "$ROOT"
mkdir -p "$ROOT/usr/lib/nexus-manager" "$ROOT/usr/bin" \
         "$ROOT/usr/share/doc/nexus-manager" "$ROOT/usr/share/applications" \
         "$ROOT/usr/lib/systemd/user" \
         "$ROOT/usr/lib/udev/rules.d"

# ⛔ WHOLESALE. Never an allow-list of names: the moment the payload gains a file class
# nobody listed, the copy ships silently incomplete and every downstream check still
# passes. Copy everything, then prune what does not belong under /usr/lib.
cp -a "$PUB/." "$ROOT/usr/lib/nexus-manager/"
rm -rf "$ROOT/usr/lib/nexus-manager/packaging"
rm -f  "$ROOT/usr/lib/nexus-manager/LICENSE" "$ROOT/usr/lib/nexus-manager/README.md"

cp LICENSE "$ROOT/usr/share/doc/nexus-manager/copyright"
cp packaging/nexus-manager.desktop packaging/nexus-manager-tray.desktop \
   "$ROOT/usr/share/applications/"
cp packaging/70-icue-nexus.rules "$ROOT/usr/lib/udev/rules.d/"
# The headless user service. The AUR package has always installed this; the .deb
# did not, so a Debian user had no way to run the daemon without a tray.
cp packaging/nexus-manager.service "$ROOT/usr/lib/systemd/user/"

# Icons, wholesale again, preserving the hicolor tree exactly as it is laid out.
find packaging/icons -type f -name '*.png' -print0 | while IFS= read -r -d '' f; do
    install -Dm644 "$f" "$ROOT/usr/share/${f#packaging/}"
done

# Thin exec wrappers rather than symlinks into /usr/lib: a wrapper is unambiguous about
# which directory the runtime resolves against, and gives one obvious place to put an
# environment fix later without touching the package layout.
for exe in nexus-manager nexus-manager-editor; do
    cat > "$ROOT/usr/bin/$exe" <<WRAP
#!/bin/sh
exec /usr/lib/nexus-manager/$exe "\$@"
WRAP
    chmod 755 "$ROOT/usr/bin/$exe"
done

echo "== deb ($DEBARCH)"
DEB="$OUT/debroot"
rm -rf "$DEB"
cp -a "$ROOT" "$DEB"
mkdir -p "$DEB/DEBIAN"
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)

# Every name below was taken from a sweep of the actual publish rather than from memory:
# objdump -p for NEEDED entries, and a strings sweep of the native and managed assemblies
# for the ones that are dlopened and therefore never appear in NEEDED at all.
#
#   NEEDED       libc, libdl, libm, libpthread, librt, libgcc_s, libstdc++, libfontconfig
#   dlopened     libX11 libXext libXi libXrandr libXcursor libXfixes libICE libSM libGL
#                (Avalonia's X11 backend - under Wayland this runs through XWayland),
#                libudev (HidSharp enumerating the hidraw node),
#                libicuuc/libicui18n (InvariantGlobalization is deliberately false),
#                libdbus-1 (the tray icon speaks StatusNotifierItem over D-Bus, and the
#                tray is how the panel is meant to be kept alive)
#
# parec is a Recommends, not a Depends: it is what the music visualizers capture the
# default output's monitor through, and everything else in the application - the whole
# sensor display, which is the point of it - works without any audio server at all.
# apt installs Recommends by default, so the visualizers work out of the box anyway.
cat > "$DEB/DEBIAN/control" <<CTRL
Package: nexus-manager
Version: $VER
Section: utils
Priority: optional
Architecture: $DEBARCH
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, libfontconfig1, libfreetype6, libicu76 | libicu74 | libicu72, libx11-6, libxext6, libxi6, libxrandr2, libxcursor1, libxfixes3, libice6, libsm6, libgl1, libudev1, libdbus-1-3
Recommends: pulseaudio-utils | pipewire-pulse, xdg-desktop-portal
Suggests: libgtk-3-0t64 | libgtk-3-0, libvulkan1
Maintainer: Nexus Manager <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/Nexus-Manager
Description: Sensor display and music visualizer for the Corsair iCUE NEXUS
 Drives the 640x48 iCUE NEXUS touchscreen directly over USB HID, with no
 vendor software involved. Renders configurable sensor readouts discovered
 from the machine itself, swipeable screens with touch buttons, animated
 backgrounds, and 32 music visualizer modes driven by the audio being played.
 Ships a GUI editor with a live preview and a headless daemon.
 .
 Not affiliated with or endorsed by Corsair.
CTRL

# The udev rule only takes effect once udev has reloaded it, and the device is usually
# already plugged in - so trigger as well as reload, or the first run after installing
# still cannot open the panel.
cat > "$DEB/DEBIAN/postinst" <<'POST'
#!/bin/sh
set -e
if [ "$1" = configure ] && command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules >/dev/null 2>&1 || true
    udevadm trigger --action=add --subsystem-match=hidraw >/dev/null 2>&1 || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications 2>/dev/null || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -qtf /usr/share/icons/hicolor 2>/dev/null || true
fi
exit 0
POST
chmod 755 "$DEB/DEBIAN/postinst"

dpkg-deb --build --root-owner-group "$DEB" "$OUT/nexus-manager$SUFFIX.deb" > /dev/null
rm -rf "$DEB" "$ROOT"

# A checksum file alongside the artifacts. Not a signature - it proves the
# download was not corrupted, not that the release is authentic - but paired
# with a reproducible build it lets anyone rebuild the tarball from the tag
# and confirm the bytes match what was published.
( cd "$OUT" && sha256sum nexus-manager-*.tar.gz nexus-manager*.deb > SHA256SUMS )


# ⛔ THE RELEASE GATE. This project is published pseudonymously, and an address
# or a build path baked into an artifact cannot be recalled once it is on a
# release page or in the AUR. The audit works from an ALLOW-LIST of the two
# approved noreply addresses plus the account name read from the system, so it
# catches an address nobody thought to look for - and so that the script itself
# carries none of what it is guarding against.
#
# It is called from HERE, rather than left as a step to remember, because a
# check that has to be remembered is a check that gets skipped on the release
# that needed it. set -e makes a failure stop the build.
echo "== identity audit"
bash packaging/identity-audit.sh "${RELEASE_RANGE:-}"

echo "== artifacts:"
ls -sh1 "$OUT" | grep -v publish
echo "== sha256 (for packaging/aur/PKGBUILD):"
sha256sum "$OUT/nexus-manager-$VER-linux-$ARCH.tar.gz" | cut -d' ' -f1
