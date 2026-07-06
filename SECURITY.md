# Security Policy

## Reporting a vulnerability

Please report security issues **privately** — do not open a public issue for a
suspected vulnerability.

- Preferred: use GitHub's **private vulnerability reporting** for this repository
  (the *Security* tab → *Report a vulnerability*).
- You will get an acknowledgement, and a fix or mitigation plan once the report
  is triaged.

Please include enough detail to reproduce: affected version, configuration
shape (sanitized), and the observed vs. expected behavior.

## Scope and threat model

RestoreGuard is a **read-only** auditor. It is designed to never write state to
an audited host, and it stores **no credentials of its own** — it rides on the
SSH setup already present on the operator machine (`ssh -o BatchMode=yes` against
your `~/.ssh/config` aliases and keys).

Because of that, the security-relevant surface is mostly:

- **Command construction** — probe commands sent over SSH. Reports of a path
  that could inject or mutate a target are in scope and treated seriously.
- **Report / config handling** — parsing untrusted remote command output and
  the local config/suppressions files.
- **Secret handling** — RestoreGuard reads references to repo passwords /
  passphrase files on the audited hosts; it must not log or emit their contents.

Out of scope: the security of the SSH setup, keys, and host access you provide —
that's yours to manage.

## Supported versions

RestoreGuard is pre-1.0 and ships from `main`. Security fixes target the latest
released version and `main`.
