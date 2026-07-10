#!/usr/bin/env bash
# Builds a .deb from a self-contained published restoreguard binary.
# Usage: build-deb.sh <binary> <version> <deb-arch: amd64|arm64> [outdir]
set -euo pipefail

BIN="$1"; VERSION="$2"; ARCH="$3"; OUT="${4:-.}"
ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT

mkdir -p "$ROOT/usr/bin" "$ROOT/usr/share/doc/restoreguard" "$ROOT/DEBIAN"
install -m 755 "$BIN" "$ROOT/usr/bin/restoreguard"

for sample in restoreguard.sample.json reporting.sample.json; do
  if [ -f "$(dirname "$0")/../$sample" ]; then
    install -m 644 "$(dirname "$0")/../$sample" \
      "$ROOT/usr/share/doc/restoreguard/$sample"
  fi
done

cat > "$ROOT/DEBIAN/control" <<EOF
Package: restoreguard
Version: $VERSION
Architecture: $ARCH
Maintainer: OfBirds <https://github.com/OfBirds/RestoreGuard>
Depends: openssh-client
Section: admin
Priority: optional
Homepage: https://github.com/OfBirds/RestoreGuard
Description: Backup-integrity and restore-drift auditor for homelabs
 Cross-checks declared vs. live state over SSH (Docker, Proxmox/PBS,
 vzdump, TrueNAS, restic, and more) and reports RED/YELLOW/GREEN per
 service. Read-only; self-contained (no .NET runtime required).
EOF

dpkg-deb --build --root-owner-group "$ROOT" "$OUT/restoreguard_${VERSION}_${ARCH}.deb"
echo "Built $OUT/restoreguard_${VERSION}_${ARCH}.deb"
