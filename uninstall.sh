#!/bin/sh
# Uninstall ndx.
#   curl -fsSL https://github.com/devlooped/ndx/releases/latest/download/uninstall.sh | sh
# Also removes leftover ndnx from the pre-rename install location.
set -eu

PREFIX="${NDX_PREFIX:-${HOME}/.local/bin}"
RID="${NDX_RID:-}"
SKIP_PATH="${NDX_SKIP_PATH:-0}"

detect_rid() {
    os=$(uname -s | tr '[:upper:]' '[:lower:]')
    arch=$(uname -m | tr '[:upper:]' '[:lower:]')

    case "$arch" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *)
            echo "ndx: unsupported architecture '$arch'" >&2
            exit 1
            ;;
    esac

    case "$os" in
        linux) echo "linux-${arch}" ;;
        darwin) echo "osx-${arch}" ;;
        mingw*|msys*|cygwin*) echo "win-${arch}" ;;
        *)
            echo "ndx: unsupported OS '$os'" >&2
            exit 1
            ;;
    esac
}

remove_file() {
    dest=$1
    if [ -e "$dest" ]; then
        rm -f "$dest"
        echo "removed ${dest}"
    fi
}

remove_empty_dir() {
    dir=$1
    if [ -d "$dir" ]; then
        rmdir "$dir" 2>/dev/null || true
    fi
}

if [ -z "$RID" ]; then
    RID=$(detect_rid)
fi

case "$RID" in
    win-*) binary=ndx.exe; legacy_binary=ndnx.exe ;;
    linux-*|osx-*) binary=ndx; legacy_binary=ndnx ;;
    *)
        echo "ndx: unsupported RID '$RID'" >&2
        exit 1
        ;;
esac

legacy_prefix="${HOME}/.local/bin"

dest="${PREFIX}/${binary}"
if [ -e "$dest" ]; then
    remove_file "$dest"
else
    echo "ndx not installed at ${dest}"
fi

remove_file "${PREFIX}/${legacy_binary}"
if [ "$PREFIX" != "$legacy_prefix" ]; then
    remove_file "${legacy_prefix}/${legacy_binary}"
fi

remove_empty_dir "$PREFIX"
if [ "$PREFIX" != "$legacy_prefix" ]; then
    remove_empty_dir "$legacy_prefix"
fi

remove_path_block() {
    file=$1
    if [ ! -f "$file" ]; then
        return 0
    fi
    if ! grep -q '# >>> ndx path >>>' "$file" 2>/dev/null \
        && ! grep -q '# >>> ndnx path >>>' "$file" 2>/dev/null; then
        return 0
    fi
    tmpfile="${file}.ndx.tmp.$$"
    awk '
        BEGIN { skip=0 }
        /# >>> ndx path >>>/ { skip=1; next }
        /# <<< ndx path <<</ { skip=0; next }
        /# >>> ndnx path >>>/ { skip=1; next }
        /# <<< ndnx path <<</ { skip=0; next }
        skip==0 { print }
    ' "$file" > "$tmpfile"
    mv "$tmpfile" "$file"
    echo "PATH removed from $file"
}

if [ "$SKIP_PATH" != "1" ]; then
    remove_path_block "${HOME}/.zshrc"
    remove_path_block "${HOME}/.bashrc"
    remove_path_block "${HOME}/.profile"
    remove_path_block "${HOME}/.config/fish/config.fish"
fi
