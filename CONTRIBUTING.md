# Contributing to RestoreGuard

Thanks for your interest in RestoreGuard (**Greylag Goose**). It's a read-only
backup-integrity and restore-drift auditor for homelabs, and contributions that
keep it accurate, safe, and boring-to-operate are very welcome.

## Ground rules

- **Read-only is sacred.** The auditor must never write state to an audited host.
  Every provider runs read-only probes over SSH (`inspect`, `list`, `find`,
  `pvesh get`, `midclt call`, `smartctl -H`, …). A change that mutates a target,
  or that could, will not be merged.
- **No AI in the audit path.** The engine is deterministic plumbing. Any
  AI-assisted feature only ever consumes the *finished* report, optionally and
  bring-your-own-key.
- **Grounded in reality.** Checks are validated against real infrastructure and
  covered by golden-file fixtures (sanitized captures). New checks/providers
  should come with fixtures and tests.

## Development setup

You need the **.NET 10 SDK**. That's it.

```sh
dotnet build RestoreGuard.slnx -c Release     # 0 warnings expected
dotnet test  RestoreGuard.slnx -c Release     # all tests must pass
```

Run the CLI from a build:

```sh
dotnet run --project src/RestoreGuard.Cli -- doctor
dotnet run --project src/RestoreGuard.Cli -- audit
```

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs build + test on
every push and pull request.

## Code style

- The build uses `TreatWarningsAsErrors` and nullable reference types — keep new
  code warning-clean.
- Formatting follows [.editorconfig](.editorconfig); `dotnet format` before
  pushing keeps diffs small.
- Match the surrounding code: comment density, naming, and idiom. Providers
  parse; checks decide RED/YELLOW/GREEN; the CLI renders. Keep those seams clean.

## Tests

- Parsers and checks are unit-tested against fixtures in
  `tests/RestoreGuard.Tests/Fixtures`. Add a sanitized fixture for any new probe
  output shape.
- A change that alters findings should update or add golden expectations, not
  loosen assertions.

## Submitting changes

1. Branch off `main`.
2. Keep commits focused; write a clear message explaining the *why*.
3. Ensure `dotnet build` and `dotnet test` are green.
4. Open a pull request against `main`.

## License

By contributing, you agree that your contributions are licensed under the
project's **AGPL-3.0-or-later** license (see [LICENSE](LICENSE)).
