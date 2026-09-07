#!/usr/bin/env bash
# Installs the desktop entry and icons for the current user.
# No root needed: everything goes under ~/.local/share.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
share="${XDG_DATA_HOME:-$HOME/.local/share}"

# Link the binaries onto PATH so the desktop entry's Exec= resolves. Symlinks
# rather than copies: a rebuild is picked up without reinstalling.
bin="$HOME/.local/bin"
mkdir -p "$bin"
root="$(cd "$here/.." && pwd)"
ln -sf "$root/src/NexusManager.Cli/bin/Release/net10.0/nexus-manager"        "$bin/nexus-manager"
ln -sf "$root/src/NexusManager.Editor/bin/Release/net10.0/nexus-manager-editor" "$bin/nexus-manager-editor"

install -Dm644 "$here/nexus-manager.desktop" "$share/applications/nexus-manager.desktop"

# Copy the WHOLE icon tree rather than naming sizes: an allow-list of filenames
# silently ships fewer than intended while every step still reports success.
find "$here/icons" -type f -name '*.png' -print0 | while IFS= read -r -d '' f; do
    rel="${f#"$here/icons/"}"
    install -Dm644 "$f" "$share/icons/$rel"
done

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -qtf "$share/icons/hicolor" 2>/dev/null || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q "$share/applications" 2>/dev/null || true
fi

echo "Installed. Icons: $(find "$share/icons/hicolor" -name 'nexus-manager.png' | wc -l) sizes."
echo "The launcher may take a moment to notice the new entry."
