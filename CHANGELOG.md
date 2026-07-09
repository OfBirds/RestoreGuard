# Changelog

All notable changes to RestoreGuard are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Report destinations (`reporting` config section)**: every audit now *delivers* its
  JSON report instead of only printing it — to a **folder** (timestamped
  `rg-report-<utc-ts>.json` + atomically-replaced `latest.json`, optional `keepLast`
  pruning), an **S3-compatible bucket** (MinIO/Garage/R2/AWS; hand-rolled SigV4, no SDK),
  and/or a **MongoDB collection** (one queryable document per report) — any combination,
  in parallel. With nothing configured, reports go to a per-user default folder
  (`Documents\RestoreGuard\reports` on Windows, `~/.local/share/restoreguard/reports`
  elsewhere; `RESTOREGUARD_REPORTS_DIR` overrides). Secrets support the `*File` pattern.
- **`r` menu entry — reporting wizard**: interactive destination setup that live-probes
  every answer (folder write test, S3 put+delete round-trip, MongoDB ping) and rewrites
  only the `reporting` section of the config.
- **`doctor` verifies report destinations** as part of preflight, and a destination
  that can't be written turns the audit run into exit `1` — delivery failures are as
  loud as findings (documented in the exit-code table).

### Changed

- `MongoDB.Driver` is now the CLI's only runtime package dependency (the mongo sink
  needs the wire protocol); the S3 sink deliberately stays dependency-free.

## [0.1.14] - 2026-07-07

### Added

- **`schemaVersion` on the `--json` report** (additive; currently `1`): the report
  contract now carries an explicit version, deliberately decoupled from RestoreGuard's
  product version — it bumps only on a *breaking* shape change (field renamed, removed,
  retyped, or given new meaning), never on additive growth. The canonical JSON Schema
  for each version lives in `contracts/restoreguard-report.v{N}.schema.json` and is the
  contract of record for downstream consumers. Existing consumers ignore the new field.
- **Full golden-snapshot test for the report shape** (`report-golden.v1.json`): replaces
  the previous 7-property spot-check, so any renamed/removed/retyped field now fails CI
  at the source instead of silently reaching a consumer.

## [0.1.13] - 2026-07-06

Six new checks, all live-verified against the development lab before release.

### Changed

- Wizard dialogue hardening (from the full-dialogue review): the docker path is
  now probed on the host; the dump method is validated at ask time (and the
  prompt now mentions `mongodump`); yes/no typos and garbage hour values are
  re-asked instead of silently meaning no/default; a rejected-then-skipped PVE
  node name no longer writes an invalid config; an empty PVE storage list warns
  that every guest will show RED; the borg repo prompt shows borg's remote
  syntax instead of restic's; the restore-canary prompt is shorter with a
  two-line explainer.

### Added

- **PBS sync jobs + proxmox-backup-client host backups** (rides on
  `pbsMaintenance`): PBS→PBS sync jobs are auto-discovered — when any exist, the
  last completed sync must succeed and be fresh (`pbs/sync-job-*`). Optional
  `hostBackups` lists bare-metal `proxmox-backup-client` backup ids that must
  have fresh snapshots under `host/` in the datastore
  (`pbs/host-backup-missing`, `/host-backup-stale`).
- **SQLite hot-copy detection** (`sqliteBackupDirs`): rsync/plain-copy backup
  folders of app data are scanned recursively for `-wal`/`-shm` files — those
  exist next to a database only while it is open, so inside a backup they prove
  the .db was copied mid-write (`sqlite/hot-copy`, RED). Wizard scans the folder
  live during setup; doctor preflights the path.
- **Generic off-site sync jobs** (`offsiteJobs`): any scheduled rclone script
  that logs the documented start/finish markers — multiple jobs, optional
  capacity probe (`rcloneRemote`), and a job whose log has no runs at all is now
  RED `offsite/never-ran` instead of silently checking nothing. `pbsOffsite`
  keeps working as the legacy single-job flavor. Wizard section parses the log
  live (shows the last run + rc before accepting); doctor preflights log + remote.
- **ZFS snapshot & replication check** (`zfsReplications`): sanoid/syncoid or
  plain `zfs send` on any SSH host. Source dataset must keep getting snapshots
  (`zfs-replication/no-snapshots`, `/snapshot-stale`); a configured replica's
  newest snapshot must be fresh too (`/replica-missing`, `/replica-stale`) —
  a dead replication looks fine because the replica keeps its old snapshots.
  Wizard section with live dataset probing + doctor preflight included.
- **Restore canary** (`fileBackups[].canaryPath`, restic/borg): every audit
  streams a configured sentinel file out of the *latest* snapshot and counts the
  bytes on the host — a real end-to-end restore drill through decryption and
  chunk reads, with nothing written and no content leaving the machine. A
  0-byte restore is RED (`restore-canary/failed`).
- **3-2-1 hygiene check** (`three-two-one/image-local-only`, YELLOW): a Proxmox
  guest whose every image backup lands on non-shared storage of its own node —
  the backup dies with the box. Backup artifacts now internally carry the node
  that physically holds them, which disambiguates same-named local storages
  (`local` exists on every node).

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

[Unreleased]: https://github.com/OfBirds/RestoreGuard/compare/v0.1.13...HEAD
[0.1.13]: https://github.com/OfBirds/RestoreGuard/compare/v0.1.12...v0.1.13
[0.1.12]: https://github.com/OfBirds/RestoreGuard/compare/v0.1.11...v0.1.12
[0.1.11]: https://github.com/OfBirds/RestoreGuard/compare/v0.1.10...v0.1.11
[0.1.10]: https://github.com/OfBirds/RestoreGuard/releases/tag/v0.1.10
