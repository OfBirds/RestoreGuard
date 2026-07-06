# RestoreGuard (Greylag Goose)

**Backup-integrity & restore-drift auditor for homelabs.** A single read-only CLI
that cross-checks what your backups *claim* against what your infrastructure
*actually is*, and prints a RED/YELLOW/GREEN report per service.

The most viscerally-described homelab failure is a backup that *reports success
daily* while silently dumping empty directories — a renamed bind-mount, a
`pg_dump` pointing at an old hostname, an offsite target that's been full for 60
days. RestoreGuard catches that class of drift before a real disaster does.

## Install

```sh
dotnet tool install -g RestoreGuard
```

Other options (self-contained binaries, `.deb`, Docker on GHCR) are on the
[releases page](https://github.com/OfBirds/RestoreGuard/releases).

## Use

```sh
restoreguard              # first run: guided setup wizard; then an interactive menu
restoreguard doctor       # preflight: verify every configured requirement per host
restoreguard audit        # the audit: colored RED/YELLOW/GREEN report
restoreguard audit --json # stable machine-readable report on stdout
```

It audits your hosts **read-only over SSH** (Docker/compose, Proxmox + PBS,
vzdump, TrueNAS, restic/borg/kopia/snapper, Home Assistant, off-site sync, SMART)
and writes **no state** to any audited host. `--json` + exit codes compose with
whatever scheduler you already run (cron, systemd timers, CI).

## Links

- **Docs / user guide:** https://ofbirds.org/docs/goose/
- **Source & issues:** https://github.com/OfBirds/RestoreGuard
- **License:** AGPL-3.0-or-later
