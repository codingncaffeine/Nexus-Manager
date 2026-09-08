#!/usr/bin/env bash
# Verifies the built .deb as a PRODUCT, not as a build step.
#
# ⛔ Why this is not optional. Every signal in a packaging pipeline reports on the STEP:
# "build green", "dpkg-deb exited 0", "install added N files" are all perfectly consistent
# with a package that cannot start. On a sibling project a packaging step that copied files
# by an allow-list of names dropped all 218 managed assemblies; makepkg exited 0, pacman
# installed 54 files without complaint, and the application died instantly. Only launching
# the artifact catches that class of fault.
#
# So this asserts an INVENTORY and then RUNS the binaries out of the extracted package.
#
#   bash packaging/test-deb.sh [path/to/.deb]
set -uo pipefail
cd "$(dirname "$0")/.."

DEB="${1:-packaging/out/nexus-manager.deb}"
[ -f "$DEB" ] || { echo "No such package: $DEB" >&2; exit 2; }

ROOT=$(mktemp -d)
trap 'rm -rf "$ROOT" "${CACHE:-}"' EXIT
FAILS=0
ok()   { echo "  OK    $*"; }
fail() { echo "  FAIL  $*"; FAILS=$((FAILS+1)); }

echo "== extracting $DEB"
dpkg-deb -x "$DEB" "$ROOT"
LIB="$ROOT/usr/lib/nexus-manager"

# ---- inventory ------------------------------------------------------------------------
# Compared against the publish tree the package was built from, so this catches a copy that
# shipped fewer files rather than merely asserting a number somebody typed in.
echo "== inventory"
PUB=packaging/out/publish
if [ -d "$PUB" ]; then
    want=$(find "$PUB" -name '*.dll' | wc -l)
    got=$(find "$LIB" -name '*.dll' | wc -l)
    [ "$got" -eq "$want" ] && ok "managed assemblies: $got of $want" \
                           || fail "managed assemblies: $got, expected $want"
else
    got=$(find "$LIB" -name '*.dll' | wc -l)
    [ "$got" -ge 200 ] && ok "managed assemblies: $got" || fail "only $got assemblies"
fi

for f in usr/bin/nexus-manager usr/bin/nexus-manager-editor \
         usr/lib/nexus-manager/nexus-manager usr/lib/nexus-manager/nexus-manager-editor \
         usr/lib/udev/rules.d/70-icue-nexus.rules \
         usr/share/applications/nexus-manager.desktop \
         usr/share/doc/nexus-manager/copyright; do
    [ -e "$ROOT/$f" ] && ok "present: $f" || fail "MISSING: $f"
done

icons=$(find "$ROOT/usr/share/icons" -name 'nexus-manager.png' 2>/dev/null | wc -l)
[ "$icons" -ge 9 ] && ok "icon sizes: $icons" || fail "icon sizes: $icons, expected 9"

# The runtime's tracing shim is the one file deliberately removed, because it is the only
# thing that would drag liblttng-ust into the dependency list.
[ ! -e "$LIB/libcoreclrtraceptprovider.so" ] && ok "tracing shim excluded" \
                                             || fail "libcoreclrtraceptprovider.so shipped"

# ---- every library the payload asks for must exist on this machine ---------------------
echo "== dependency resolution"
# ⛔ The library cache is snapshotted ONCE, to a file, and matched against that.
#
# The obvious form - `ldconfig -p | grep -q "$lib"` inside the loop - is broken
# under `set -o pipefail`, and broken INTERMITTENTLY, which is worse. grep -q exits
# the moment it matches; ldconfig then writes into a closed pipe, takes SIGPIPE and
# exits 141; and pipefail reports the whole pipeline as failed even though the match
# succeeded. That reported seven perfectly resolvable libraries as missing while the
# very same package launched and ran four ways. A check that fires on correct work is
# worse than no check, because it trains you to ignore it.
CACHE=$(mktemp); ldconfig -p 2>/dev/null > "$CACHE" || true
missing=0
while read -r lib; do
    case "$lib" in libmscordaccore.so|libhostpolicy.so|libhostfxr.so|libcoreclr.so|libclrjit.so) continue ;; esac
    [ -e "$LIB/$lib" ] && continue                       # the bundle brings it
    awk -v n="$lib" '$1==n{found=1} END{exit !found}' "$CACHE" ||
        { echo "     unresolved: $lib"; missing=$((missing+1)); }
done < <(find "$LIB" -type f \( -name '*.so' -o -executable \) -print0 |
         xargs -0 -r -n1 objdump -p 2>/dev/null | awk '/NEEDED/{print $2}' | sort -u)
[ "$missing" -eq 0 ] && ok "every NEEDED library resolves" || fail "$missing unresolved libraries"

# ---- launch the product ---------------------------------------------------------------
#
# ⛔ Run from the EXTRACTED PACKAGE, with DOTNET_ROOT scrubbed. A self-contained bundle that
# silently fell back to the development machine's SDK would pass every check above and fail
# on any machine without one.
echo "== launch"
export DOTNET_ROOT= DOTNET_MULTILEVEL_LOOKUP=0
unset DOTNET_ROOT

out=$(timeout 90 "$LIB/nexus-manager" visualizer --selftest 2>&1); rc=$?
if [ $rc -eq 0 ] && printf '%s' "$out" | grep -q "PASSED"; then
    ok "nexus-manager visualizer --selftest (runtime + DSP)"
else
    fail "visualizer --selftest exited $rc"
    printf '%s\n' "$out" | tail -5 | sed 's/^/        /'
fi

out=$(timeout 90 "$LIB/nexus-manager" sensors 2>&1); rc=$?
if [ $rc -eq 0 ] && printf '%s' "$out" | grep -qE "[0-9]+ sensors across"; then
    ok "nexus-manager sensors ($(printf '%s' "$out" | grep -oE '[0-9]+ sensors across [0-9]+ devices'))"
else
    fail "sensors exited $rc"
fi

# Renders a real frame through SkiaSharp to a PNG. This is what proves libSkiaSharp loaded
# and found fontconfig - the one native dependency that is NEEDED rather than dlopened, and
# the one most likely to be absent on a minimal install.
png="$ROOT/preview.png"
out=$(cd "$ROOT" && timeout 90 "$LIB/nexus-manager" preview --out "$png" 2>&1); rc=$?
if [ -s "$png" ]; then
    ok "nexus-manager preview -> $(stat -c%s "$png") byte PNG (SkiaSharp + fontconfig)"
else
    fail "preview produced no PNG (exit $rc)"
    printf '%s\n' "$out" | tail -5 | sed 's/^/        /'
fi

if [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
    out=$(timeout 120 "$LIB/nexus-manager-editor" --selftest 2>&1); rc=$?
    if [ $rc -eq 0 ] && printf '%s' "$out" | grep -q "PASS"; then
        ok "nexus-manager-editor --selftest (Avalonia + every view)"
    elif printf '%s' "$out" | grep -q "already running"; then
        echo "  SKIP  editor self-test - another instance holds the panel"
    else
        fail "editor --selftest exited $rc"
        printf '%s\n' "$out" | tail -5 | sed 's/^/        /'
    fi
else
    echo "  SKIP  editor self-test - no display"
fi

echo
if [ "$FAILS" -eq 0 ]; then echo "  PACKAGE OK"; exit 0; fi
echo "  PACKAGE FAILED ($FAILS)"; exit 1
