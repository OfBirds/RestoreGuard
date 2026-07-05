#!/usr/bin/env sh
# RestoreGuard installer: downloads the self-contained binary for this OS/arch
# from GitHub releases. No .NET required.
#
#   curl -fsSL https://raw.githubusercontent.com/OfBirds/RestoreGuard/main/scripts/install.sh | sh
#
# Overrides:
#   RG_VERSION=v0.1.7        install a specific version (default: latest)
#   RG_BIN_DIR=~/.local/bin  install directory
#   RG_BASE_URL=...          alternate download base (testing/mirrors)
set -eu

REPO="OfBirds/RestoreGuard"
BIN_DIR="${RG_BIN_DIR:-$HOME/.local/bin}"

os=$(uname -s); arch=$(uname -m)
case "$os" in
  Linux)  os_id=linux ;;
  Darwin) os_id=osx ;;
  *) echo "Unsupported OS: $os (on Windows, use the .zip from the releases page)"; exit 1 ;;
esac
case "$arch" in
  x86_64|amd64)  arch_id=x64 ;;
  aarch64|arm64) arch_id=arm64 ;;
  *) echo "Unsupported architecture: $arch"; exit 1 ;;
esac
rid="$os_id-$arch_id"

if [ -z "${RG_VERSION:-}" ]; then
  RG_VERSION=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
    | grep '"tag_name"' | head -1 | cut -d'"' -f4)
  [ -n "$RG_VERSION" ] || { echo "Could not determine the latest release."; exit 1; }
fi

base="${RG_BASE_URL:-https://github.com/$REPO/releases/download/$RG_VERSION}"
file="restoreguard-$RG_VERSION-$rid.tar.gz"

echo "Installing restoreguard $RG_VERSION ($rid) to $BIN_DIR ..."
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
curl -fsSL "$base/$file" -o "$tmp/$file"
tar -xzf "$tmp/$file" -C "$tmp"
mkdir -p "$BIN_DIR"
install -m 755 "$tmp/restoreguard" "$BIN_DIR/restoreguard"

echo "Installed: $BIN_DIR/restoreguard"
case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
  *) echo "NOTE: $BIN_DIR is not on your PATH — add:  export PATH=\"\$PATH:$BIN_DIR\"" ;;
esac
"$BIN_DIR/restoreguard" help | head -1
