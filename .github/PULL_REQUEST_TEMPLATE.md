<!-- Thanks for contributing to RestoreGuard! Keep the diff focused and the "why" clear. -->

## What & why

<!-- What does this change, and what problem does it solve? -->

## Checklist

- [ ] `dotnet build RestoreGuard.slnx -c Release` is clean (0 warnings)
- [ ] `dotnet test RestoreGuard.slnx -c Release` passes
- [ ] New/changed checks or parsers have fixtures + tests
- [ ] The audit stays **read-only** — no state written to any audited host
- [ ] `CHANGELOG.md` updated if this is user-visible
