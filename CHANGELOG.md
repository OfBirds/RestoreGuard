# Changelog

All notable changes to RestoreGuard are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.12] - 2026-07-06

### Added

- NuGet.org listing now shows a readme, icon, and project URL.
- README status badges (CI, NuGet, release, license).
- Dependabot (`dependabot.yml`): weekly GitHub Actions + NuGet updates.
- Issue forms (bug / feature) and a pull-request template.

### Changed

- Release version is now taken from the git tag (`-p:Version` in the release
  pipeline) instead of a hardcoded csproj `<Version>`, so a published package can
  no longer disagree with its tag. The csproj value is a local-dev default only.

## [0.1.11] - 2026-07-06

### Added

- Continuous-integration workflow (`ci.yml`): build + test on every push and
  pull request.
- Contributor scaffolding: `CONTRIBUTING.md`, `SECURITY.md`,
  `CODE_OF_CONDUCT.md`, `.editorconfig`, and this changelog.

### Changed

- NuGet.org publishing now uses **trusted publishing (OIDC)** — a short-lived,
  keyless token minted at release time instead of a stored API key. This is the
  first release to actually reach NuGet.org (`dotnet tool install -g RestoreGuard`).
- Release pipeline replaces an existing GitHub Release on re-tag instead of
  failing.

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

[Unreleased]: https://github.com/OfBirds/RestoreGuard/compare/v0.1.12...HEAD
[0.1.12]: https://github.com/OfBirds/RestoreGuard/compare/v0.1.11...v0.1.12
[0.1.11]: https://github.com/OfBirds/RestoreGuard/compare/v0.1.10...v0.1.11
[0.1.10]: https://github.com/OfBirds/RestoreGuard/releases/tag/v0.1.10
