#!/bin/sh
# Install ndnx from GitHub Releases.
#   curl -fsSL https://github.com/devlooped/ndnx/releases/latest/download/install.sh | sh
set -eu

REPO="${NDNX_REPO:-devlooped/ndnx}"
VERSION="${NDNX_VERSION:-}"
PREFIX="${NDNX_PREFIX:-${HOME}/.local/bin}"
ARCHIVE="${NDNX_ARCHIVE:-}"
RID="${NDNX_RID:-}"
SKIP_PATH="${NDNX_SKIP_PATH:-0}"

detect_rid() {
    os=$(uname -s | tr '[:upper:]' '[:lower:]')
    arch=$(uname -m | tr '[:upper:]' '[:lower:]')

    case "$arch" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *)
            echo "ndnx: unsupported architecture '$arch'" >&2
            exit 1
            ;;
    esac

    case "$os" in
        linux) echo "linux-${arch}" ;;
        darwin) echo "osx-${arch}" ;;
        mingw*|msys*|cygwin*) echo "win-${arch}" ;;
        *)
            echo "ndnx: unsupported OS '$os'" >&2
            exit 1
            ;;
    esac
}

github_json() {
    url=$1
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL -H "Accept: application/vnd.github+json" "$url"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO- --header="Accept: application/vnd.github+json" "$url"
    else
        echo "ndnx: need curl or wget" >&2
        exit 1
    fi
}

download() {
    url=$1
    dest=$2
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$url" -o "$dest"
    else
        wget -qO "$dest" "$url"
    fi
}

json_string() {
    # Extract the first JSON string value for a given key without jq.
    key=$1
    sed -n "s/.*\"${key}\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p" | head -n 1
}

verify_sha256() {
    file=$1
    expected=$2
    expected=$(printf '%s' "$expected" | tr '[:upper:]' '[:lower:]' | awk '{print $1}')
    if command -v sha256sum >/dev/null 2>&1; then
        actual=$(sha256sum "$file" | awk '{print $1}')
    elif command -v shasum >/dev/null 2>&1; then
        actual=$(shasum -a 256 "$file" | awk '{print $1}')
    elif command -v openssl >/dev/null 2>&1; then
        actual=$(openssl dgst -sha256 "$file" | awk '{print $NF}')
    else
        echo "ndnx: no sha256 tool found (sha256sum, shasum, or openssl)" >&2
        exit 1
    fi
    actual=$(printf '%s' "$actual" | tr '[:upper:]' '[:lower:]')
    if [ "$actual" != "$expected" ]; then
        echo "ndnx: SHA256 mismatch for $(basename "$file")" >&2
        echo "  expected: $expected" >&2
        echo "  actual:   $actual" >&2
        exit 1
    fi
}

if [ -z "$RID" ]; then
    RID=$(detect_rid)
fi

case "$RID" in
    win-*) binary=ndnx.exe; ext=zip ;;
    linux-*|osx-*) binary=ndnx; ext=tar.gz ;;
    *)
        echo "ndnx: unsupported RID '$RID'" >&2
        exit 1
        ;;
esac

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT INT TERM

if [ -z "$ARCHIVE" ]; then
    if [ -n "$VERSION" ]; then
        tag=$VERSION
        case "$tag" in
            v*) ;;
            *) tag="v${tag}" ;;
        esac
        version=${tag#v}
    else
        json=$(github_json "https://api.github.com/repos/${REPO}/releases/latest")
        tag=$(printf '%s' "$json" | json_string tag_name)
        if [ -z "$tag" ]; then
            echo "ndnx: could not resolve latest release of ${REPO}" >&2
            exit 1
        fi
        version=${tag#v}
    fi

    name="ndnx-${version}-${RID}.${ext}"
    base="https://github.com/${REPO}/releases/download/${tag}"
    ARCHIVE="${tmp}/${name}"
    download "${base}/${name}" "$ARCHIVE"
    download "${base}/${name}.sha256" "${ARCHIVE}.sha256"
    expected=$(awk '{print $1}' "${ARCHIVE}.sha256")
    verify_sha256 "$ARCHIVE" "$expected"
else
    if [ -f "${ARCHIVE}.sha256" ]; then
        expected=$(awk '{print $1}' "${ARCHIVE}.sha256")
        verify_sha256 "$ARCHIVE" "$expected"
    fi
fi

extract="${tmp}/extract"
mkdir -p "$extract"
case "$ext" in
    zip)
        if command -v unzip >/dev/null 2>&1; then
            unzip -o -q "$ARCHIVE" -d "$extract"
        else
            echo "ndnx: unzip is required to extract Windows archives" >&2
            exit 1
        fi
        ;;
    tar.gz)
        tar -xzf "$ARCHIVE" -C "$extract"
        ;;
esac

if [ ! -f "${extract}/${binary}" ]; then
    echo "ndnx: archive did not contain ${binary}" >&2
    exit 1
fi

mkdir -p "$PREFIX"
# Prefer install(1) so the dest is replaced atomically and marked executable.
if command -v install >/dev/null 2>&1; then
    install -m 0755 "${extract}/${binary}" "${PREFIX}/${binary}"
else
    cp "${extract}/${binary}" "${PREFIX}/${binary}"
    chmod 0755 "${PREFIX}/${binary}"
fi

echo "installed ${PREFIX}/${binary}"

if [ "$SKIP_PATH" != "1" ]; then
    case ":${PATH}:" in
        *":${PREFIX}:"*) ;;
        *)
            echo "add ${PREFIX} to PATH, for example:" >&2
            echo "  export PATH=\"${PREFIX}:\$PATH\"" >&2
            ;;
    esac
fi
