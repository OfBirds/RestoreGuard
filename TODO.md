# TODO

## Dashboard-registration drift check (new check family — service coverage)

**Added 2026-07-12. Not implemented yet — currently a bash block in the homelab
daily audit; port it into the RestoreGuard arsenal as a proper check.**

The homelab now runs a navigation dashboard (Homepage/gethomepage on k3s,
`http://dash.lab`) whose service registry is a `services.yaml` inside a k8s
ConfigMap (`k8s/homepage/homepage.yaml` in the homelab repo). The interim drift
check lives in `homelab/scripts/homelab-audit.sh` (block
`# --- Dashboard (Homepage on k3s, http://dash.lab) registration drift ---`):

- **Registry source:** `kubectl -n homepage get configmap homepage-config
  -o go-template='{{index .data "services.yaml"}}'` over SSH to the k3s master.
- **Discovery:** `docker ps --format '{{.Names}}\t{{.Ports}}'` on each Docker
  host (.98, .55, .118, .204), keeping only containers with published host ports.
- **Match rule:** a container is *registered* when services.yaml references it
  by `container: <name>` (docker socket-proxy integration) or by any of its
  published `<host-ip>:<port>` pairs (href/siteMonitor). Otherwise it is drift.
- **Suppression:** a hardcoded `DASH_IGNORE` regex for infra/sidecars
  (exporters, DBs, otel, bench containers).

Why it belongs in RestoreGuard: it is the same shape as the existing coverage
checks (deterministic, read-only, SSH probes, pre-verdicted findings with
evidence + suggestedAction), and the bash version has known weaknesses the
engine would fix properly:

- `DASH_IGNORE` is a regex in a cron script — should be entries in
  `suppressions.json` with owner/expiry semantics.
- Name-only suppression can't distinguish the prod `technitium` container
  (boombox) from the benchmark leftovers with the same name on .98/.118 —
  needs host-scoped suppression.
- No IPv6-only published ports, no k8s-native workloads on the discovery side
  (ingresses with `gethomepage.dev/enabled` annotations self-register, but a
  k3s Service/NodePort without an ingress would be invisible).
- Verdict idea: **yellow** = running user-facing service not on the dashboard;
  **red** = dashboard itself unreachable (http != 200) or registry unreadable.

Per `.claude/skills/surface-coverage/SKILL.md`, implementing this touches the
wizard (new check family opt-in + probe), doctor preflight (k8s/ConfigMap
reachability), config validation, docs, both sample-json copies, README,
CHANGELOG, tests, and regenerated wizard transcripts.
