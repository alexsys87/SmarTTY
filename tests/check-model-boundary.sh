#!/usr/bin/env bash
# Checks the boundary of the model: the logic folders must not refer to WinForms, GDI+ (System.Drawing, including
# Point, Color, Rectangle and Size), WPF, or to the view folders (SmarTTY.Controls, SmarTTY.Forms, SmarTTY.GUI).
#
#   tests/check-model-boundary.sh
#
# Files listed in tests/model-boundary-allowlist.txt are the known violations and are skipped. The list may only shrink:
# the script also fails when a listed file is already clean or no longer exists, so the list cannot go stale.
# The compiler cannot see all of it (Point and Color compile on any platform), which is why this is a text search;
# tests/ModelBuildTests adds the check that the folders compile without the Windows-only types.
set -u

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 2

folders=(Terminal TextEditing CodeEditing Completion)
allowlist="tests/model-boundary-allowlist.txt"
# System.Windows also covers System.Windows.Forms and every WPF namespace, written with or without a using.
pattern='System\.Windows|System\.Drawing|SmarTTY\.(Controls|Forms|GUI)\b'

if [ ! -f "$allowlist" ]; then
    echo "FAIL: $allowlist not found"
    exit 2
fi

allowed="$(sed -e 's/\r$//' -e '/^[[:space:]]*#/d' -e '/^[[:space:]]*$/d' "$allowlist" | sort -u)"
violating="$(grep -rlE --include='*.cs' "$pattern" "${folders[@]}" | sed 's/\r$//' | sort -u)"

errors=0

# A violation that is not in the list.
while IFS= read -r file; do
    [ -z "$file" ] && continue
    if ! grep -qxF "$file" <<<"$allowed"; then
        echo "FAIL: $file refers to UI types or view folders and is not in $allowlist:"
        grep -nE "$pattern" "$file" | sed 's/^/        /'
        errors=$((errors + 1))
    fi
done <<<"$violating"

# A listed file that no longer needs to be listed.
while IFS= read -r file; do
    [ -z "$file" ] && continue
    case "$file" in
        Terminal/*|TextEditing/*|CodeEditing/*|Completion/*) ;;
        *)
            echo "FAIL: $file in $allowlist is outside the logic folders"
            errors=$((errors + 1))
            continue
            ;;
    esac
    if [ ! -f "$file" ]; then
        echo "FAIL: $file is listed in $allowlist but does not exist: remove it from the list"
        errors=$((errors + 1))
    elif ! grep -qxF "$file" <<<"$violating"; then
        echo "FAIL: $file is listed in $allowlist but is already clean: remove it from the list"
        errors=$((errors + 1))
    fi
done <<<"$allowed"

listed="$(grep -c . <<<"$allowed")"
if [ "$errors" -ne 0 ]; then
    echo "Model boundary check failed: $errors problem(s)."
    exit 1
fi
echo "PASS: model boundary is intact; $listed known violation(s) in $allowlist."
