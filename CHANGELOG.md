# Changelog

All notable changes to RestoreGuard are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.10] - 2026-07-06

First public snapshot of RestoreGuard (**Greylag Goose**) — a read-only
backup-integrity and restore-drift auditor for homelabs.

### Added

- Read-only audit engine that cross-checks declared vs. live infrastructure and
  reports RED/YELLOW/GREEN per service, with `audit`, `doctor`, `--json`, a
  first-run setup wizard, and an interactive menu.
- Checks: mount drift, stale/config-hash drift, logical DB dump coverage
  (with method-mismatch detection), image backups (Proxmox PBS/vzdump), file-level
  backup tools (restic, borg, kopia, snapper, Home Assistant, archive dirs),
  TrueNAS (ZFS snapshots, cloud-sync, pool health/scrub), off-site freshness and
  capacity, SMART disk health, and suppression hygiene.
- First-class suppressions (`suppressions.json`): reported in their own section,
  never silently dropped, expiring on their review date.
- Cron-friendly exit codes (`0` green/yellow, `1` a RED finding or provider
  error, `2` config/preflight problem).
- Distribution: self-contained binaries (linux/macOS/windows), Debian package,
  multi-arch Docker image on GHCR, and a .NET global tool.

### Fixed

- Interactive-audit freeze on Windows caused by concurrent `ssh` children
  competing for the console input handle; child stdin is now closed (`ssh -n`
  equivalent). Added live per-probe progress to stderr, a 120s hard timeout per
  SSH command, and graceful `Ctrl+C` cancellation that still emits a partial
  report.

[Unreleased]: https://github.com/OfBirds/RestoreGuard/compare/v0.1.10...HEAD
[0.1.10]: https://github.com/OfBirds/RestoreGuard/releases/tag/v0.1.10
