#!/usr/bin/env bash
# Fails a release if anything but this project's pseudonymous identity appears in
# what is about to be published.
#
#   bash packaging/identity-audit.sh [git-range]
#
# ⛔ THIS FILE IS PUBLIC, so it must never contain the addresses it is guarding
# against - writing them here would publish the very thing it exists to keep
# out. It therefore works from an ALLOW-LIST of the two approved noreply
# addresses, plus the CURRENT ACCOUNT's own name read from the system at run
# time. That is also strictly stronger than a deny-list: it catches an address
# nobody thought to add to a list.
#
# What it checks:
#   1. every email-shaped string in the tracked source, docs and packaging
#   2. every email-shaped string in the assemblies WE build
#   3. the local account name and home directory anywhere in the artifacts -
#      a build path baked into a binary leaks a username into every stack trace
#      a user ever pastes (PathMap in Directory.Build.props exists for this, and
#      this is the check that it is still working)
#   4. the author AND committer of every commit in the range being released
set -euo pipefail
cd "$(dirname "$0")/.."

RANGE="${1:-}"
OUT=packaging/out
fail=0
bad() { printf 'identity: FAIL %s\n' "$*" >&2; fail=1; }
ok()  { printf 'identity: ok   %s\n' "$*"; }

# The only identities allowed to appear anywhere public. The numbered form is
# what the GitHub repo commits with; the plain form is what the AUR package and
# the .deb maintainer field carry. aur@aur.archlinux.org is a SERVICE address -
# the ssh remote packages are pushed to - not a person, and it is in a comment
# in the PKGBUILD telling a reader where the package lives.
# noreply@github.com is GitHub's own web-flow COMMITTER, which is what signs a
# commit made through the web UI - it appears on this repository's initial
# commit, whose AUTHOR is correctly the project identity. Not a person either.
ALLOWED='^(codingncaffeine|[0-9]+\+codingncaffeine)@users\.noreply\.github\.com$|^noreply@anthropic\.com$|^aur@aur\.archlinux\.org$|^noreply@github\.com$'
EMAIL='[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'

# The account this is being built on. Read, never written down - the point is
# that this script stays publishable.
ACCOUNT=$(id -un)
HOMEDIR=$HOME

# --- 1. tracked text -------------------------------------------------------
found=$(git ls-files -z \
        | xargs -0 grep -hoE "$EMAIL" 2>/dev/null \
        | sort -u | grep -Ev "$ALLOWED" || true)
if [ -n "$found" ]; then
    bad "tracked files carry an address that is not the project identity:"
    printf '  %s\n' $found >&2
else
    ok "tracked files carry only the project identity"
fi

# --- 2. our own assemblies -------------------------------------------------
# Deliberately NOT the whole publish tree: the bundled .NET runtime ships
# third-party notices with real addresses in them, which are theirs and not
# ours to strip. Only what this repository compiles is in scope.
if [ -d "$OUT/publish" ]; then
    found=$(find "$OUT/publish" -maxdepth 1 \( -name 'NexusManager*.dll' -o -name 'nexus-manager' \
                -o -name 'nexus-manager-editor' -o -name '*.json' \) -print0 \
            | xargs -0 grep -haoE "$EMAIL" 2>/dev/null \
            | sort -u | grep -Ev "$ALLOWED" || true)
    if [ -n "$found" ]; then
        bad "a built assembly carries an address that is not the project identity:"
        printf '  %s\n' $found >&2
    else
        ok "built assemblies carry only the project identity"
    fi

    # --- 3. build paths in the artifacts -----------------------------------
    # ⛔ THE ACCOUNT NAME IS MATCHED ONLY AS A PATH SEGMENT. Matching it as a
    # bare word fires on correct work: on a GitHub runner the account is
    # literally "runner", a word that occurs inside
    # System.Text.RegularExpressions.dll and in our own assemblies, and this
    # check failed a build that had no leak in it at all. A check that goes
    # red on correct work is one that gets switched off, so it is made
    # precise rather than silenced.
    hits=$(grep -ral -e "/$ACCOUNT/" -e "$HOMEDIR/" "$OUT/publish" 2>/dev/null || true)
    if [ -n "$hits" ]; then
        bad "the build account's path is baked into:"
        printf '  %s\n' $hits >&2
        bad "(check <PathMap> in Directory.Build.props)"
    else
        ok "no build path for this account in the artifacts"
    fi

    # And the account-INDEPENDENT half, which is the one that generalises: any
    # absolute home-shaped path inside the assemblies this repository compiles.
    # It catches a leak from a machine whose account name we never knew, and it
    # is what PathMap exists to prevent.
    leaked=$(find "$OUT/publish" -maxdepth 1 \( -name 'NexusManager*.dll' -o -name 'nexus-manager' \
                 -o -name 'nexus-manager-editor' -o -name 'nexus-manager*.dll' \) -print0 \
             | xargs -0 grep -haoE '(/home|/root|/Users|/run/media)/[A-Za-z0-9._-]+/' 2>/dev/null \
             | sort -u || true)
    if [ -n "$leaked" ]; then
        bad "an assembly we build carries an absolute build path:"
        printf '  %s\n' $leaked >&2
        bad "(check <PathMap> in Directory.Build.props)"
    else
        ok "no absolute build paths in the assemblies we compile"
    fi
else
    ok "no publish tree yet - artifact checks skipped"
fi

# --- 4. commit identity ----------------------------------------------------
if [ -n "$RANGE" ]; then
    found=$(git log --format='%ae%n%ce' "$RANGE" | sort -u | grep -Ev "$ALLOWED" || true)
    if [ -n "$found" ]; then
        bad "a commit in $RANGE is authored or committed by:"
        printf '  %s\n' $found >&2
    else
        ok "every commit in $RANGE uses the project identity"
    fi
fi

[ "$fail" -eq 0 ] || { echo "identity: REFUSING TO RELEASE" >&2; exit 1; }
echo "identity: clean"
