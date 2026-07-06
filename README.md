# RestoreGuard — backup-integrity & restore-drift auditor

[![CI](https://github.com/OfBirds/RestoreGuard/actions/workflows/ci.yml/badge.svg)](https://github.com/OfBirds/RestoreGuard/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/RestoreGuard.svg?logo=nuget)](https://www.nuget.org/packages/RestoreGuard)
[![Release](https://img.shields.io/github/v/release/OfBirds/RestoreGuard?logo=github)](https://github.com/OfBirds/RestoreGuard/releases)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)

> **Greylag Goose** in the [ofbirds](https://ofbirds.org) flock — the watch-bird:
> the sacred geese of Juno whose alarm saved Rome from the night attack on the
> Capitol. First app in the **homelab tooling** category (RestoreGuard stays the
> technical name on GitHub/GHCR; Greylag Goose is the product identity).

**📘 User guide: https://ofbirds.org/docs/goose/** · Product page: https://ofbirds.org/goose

---

## The pain

The most viscerally-described homelab failure: backups that *report success
daily* while silently dumping empty directories — a renamed bind-mount during a
compose refactor, a pg_dump pointing at an old hostname after a VM→LXC
migration, an offsite target that's been full and silently rejecting snapshots
for 60 days. People only discover it during a real disaster.

## What it checks

One read-only CLI that cross-checks what your backups *claim* against what your
infrastructure *actually is*, and prints a RED/YELLOW/GREEN report per service:

- **Mount drift** — docker compose declared bind-mounts/volumes vs. what the
  container *actually* mounts (the classic "backing up an empty folder" cause).
- **Stale config** — a container still running an old compose config
  (config-hash drift), so the next restart silently changes behavior.
- **Logical DB dump coverage** — every live prod database container has a fresh,
  non-empty dump; **method mismatch** flagged (e.g. `pg_dumpall` against a
  DocumentDB image, `mysqldump` vs `pg_dump` per engine); naming-convention
  contracts enforced.
- **Image backups (Proxmox)** — every VM/LXC is covered by PBS or vzdump, backups
  are fresh, orphan backups flagged; PBS datastore GC ran recently, verify jobs
  exist and the last verification completed, passed, and is fresh.
- **File-level backup tools** — restic, borg, kopia, snapper (btrfs), Home
  Assistant native backups, or plain archive directories: snapshots exist, are
  fresh, and aren't suspiciously small.
- **Restore canary** — snapshots existing ≠ backups restoring. Opt a restic/borg
  source into a per-audit restore drill: a sentinel file is streamed out of the
  *latest* snapshot and byte-counted on the host (nothing written, no content
  over the wire). 0 bytes back — wrong passphrase, corrupt chunks, path fell out
  of the backup — is RED.
- **3-2-1 hygiene (Proxmox)** — a guest whose *every* image backup sits on
  non-shared storage of its own node gets flagged: one disk or host failure
  takes the guest and all its copies together.
- **ZFS snapshots & replication** — sanoid/syncoid or plain `zfs send` on any
  host: the snapshot job still produces, and the replica's newest snapshot is
  fresh (a dead replication *looks* fine — the replica keeps its old snapshots).
- **TrueNAS** — ZFS snapshot freshness, cloud-sync tasks succeeding, top-level
  datasets that never leave the box, pool health + scrub age.
- **Off-site freshness & capacity** — every scheduled rclone sync job actually
  ran, succeeded, and is recent (a job that never ran is RED, not invisible);
  the destination isn't silently full.
- **Disk health** — SMART status on the hypervisors.
- **Suppression hygiene** — accepted-risk entries are first-class and fail loud:
  expired or dead suppressions become findings themselves, never silent.

Everything is **read-only**: no state is written to any audited host. There is no
AI in the audit path — the engine is deterministic plumbing, and any AI-assisted
presentation will only ever consume the finished report, optionally and
bring-your-own-key.

---

## Running it — what you need on your side

*(The full user guide is the Antora component in [docs/](docs) — published on the
ofbirds docs site under `goose`. The README keeps this condensed version.)*

RestoreGuard is a **single CLI** that audits your infrastructure **read-only over
SSH**. It stores no credentials of its own — it rides on the SSH setup you already
have. You need:

### 1. On the operator machine (where the CLI runs)

- **OpenSSH client** on PATH (`ssh`). (No .NET needed for the released binaries;
  see install options below.)
- **SSH aliases + keys.** Every `alias` in the config must resolve via
  `~/.ssh/config` with **key-based, passwordless** auth. The audit runs
  `ssh -o BatchMode=yes`, which *cannot answer prompts*, so:
  - the key must not have an interactive passphrase prompt (use an agent), and
  - **connect to each host once manually first** so its host key lands in
    `known_hosts` — a first-contact prompt fails the audit.

### 2. On the audited hosts (per config section — all sections optional)

| Config section | Runs on the host | SSH user needs |
|---|---|---|
| `dockerHosts` | `docker inspect`, `docker compose config` | Docker daemon access (root or `docker` group); compose plugin ≥ 2.17; read access to compose project dirs + env files |
| `logicalDbBackup` | `find` over the dump dir | read access to the dump directory |
| `pveNodes` | `pvesh get` (guests, storage, backup content — PBS *and* vzdump dir storages) | root on the PVE node (pvesh) |
| `fileBackups` |  per kind: `restic snapshots`, `borg list`, `kopia snapshot list`, `snapper list`, `find` over an archive dir, or `qm guest exec … ha backups` | restic/borg: repo + password/passphrase file readable on the host; dir: readable path; haos: qemu guest agent enabled in the HA VM, root on its PVE host |
| `trueNas` | `midclt call` (pools, datasets, snapshots, cloud-sync) | TrueNAS admin user |
| `pbsOffsite` | `tail` the sync log, `rclone about` | read the log; the host's rclone remote must authenticate |
| `pbsMaintenance` | `pct exec <CT> -- proxmox-backup-manager` | root on the PVE host that runs the PBS container |
| `smartHosts` | `smartctl -H` | root (raw device access); smartmontools installed |

Targets are assumed Linux-ish with standard tools (GNU `find`, `awk`, `tail`).

### 3. Install and run

**No .NET required** — binaries are fully self-contained. Pick your poison
(all built by the tag-triggered release pipeline in `.github/workflows/release.yml`):

```sh
# One-liner (Linux/macOS): detects OS/arch, installs to ~/.local/bin
curl -fsSL https://raw.githubusercontent.com/OfBirds/RestoreGuard/main/scripts/install.sh | sh

# Debian/Ubuntu package
sudo dpkg -i restoreguard_<version>_amd64.deb    # from the releases page

# Docker (image on ghcr; mount SSH config read-only + a work dir with your
# restoreguard.json — /work is the cwd, so the config is found like on a host;
# use `-c /work/other.json` for a custom name)
docker run --rm -v ~/.ssh:/root/.ssh:ro -v $PWD:/work \
  ghcr.io/ofbirds/restoreguard doctor

# Or just grab the tar.gz/zip for your platform from the releases page
```

For .NET users, the global-tool route also works
(`dotnet tool install -g RestoreGuard` once on NuGet.org; from a local build:
`dotnet pack src/RestoreGuard.Cli -c Release -o nupkg && dotnet tool install -g
--add-source ./nupkg RestoreGuard`).

> **Linux note (dotnet-tool route only):** if your `dotnet` came from a manual
> install rather than a package manager, export
> `DOTNET_ROOT=$(dirname $(readlink -f $(which dotnet)))` and add
> `~/.dotnet/tools` to PATH. The standalone binaries/deb/Docker need none of this.

Then, from any directory:

```sh
restoreguard             # first run: guided setup wizard writes restoreguard.json;
                         # after that: a small interactive menu (audit/doctor/json)
restoreguard doctor      # preflight: verifies every configured requirement per host
restoreguard audit       # the audit: colored RED/YELLOW/GREEN report
restoreguard audit --json   # stable machine-readable report on stdout
restoreguard help
```

`-c/--config <path>` selects a config file (default `./restoreguard.json`);
`restoreguard.sample.json` is the annotated template for the advanced sections the
wizard doesn't cover.

Alternatively, build a standalone binary yourself with the .NET 10 SDK (swap the
RID for `win-x64` / `osx-arm64` as needed):

```sh
dotnet publish src/RestoreGuard.Cli -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o dist
dist/RestoreGuard.Cli audit
```

- **`--json`** is the integration surface (findings, suppressed findings, active
  suppressions, provider errors, counts). There is deliberately **no daemon or
  HTTP endpoint** — one-shot CLI + JSON + exit codes compose with the scheduler
  you already run (cron, systemd timers, CI).
- **Exit codes:** `0` all green/yellow, `1` at least one RED finding **or** a
  provider error (partial discovery), `2` config/preflight problem. Cron-friendly.
- **Suppressions** (`suppressions.json`): a list of `{host, service, ruleId,
  reason, decidedOn, expires?, retriggerCondition?}`. Suppressed findings are
  reported in their own section, never silently dropped, and expire on their
  review date.

RestoreGuard is developed against a **real homelab** — every provider and check was
grounded in live probes of actual infrastructure before it shipped, and the
golden-file test fixtures are (sanitized) captures from that lab.

## License

**AGPL-3.0-or-later** — see [LICENSE](LICENSE). The core auditor is and stays
free software.
