# Contributing

Thanks for taking the time to improve webhook-relay.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start PostgreSQL through Testcontainers; `docker compose up` runs the demo)
- Node 22 for the Node sample's tests; Python 3.12 (or the `python:3.12-slim` image) for the Python sample's tests

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit, property, golden and integration tests (Docker running)
   dotnet format --verify-no-changes
   (cd samples/receiver-node && npm test)
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history readable and make
the hand-written CHANGELOG easy to assemble at release time:

- `feat:` new capability (minor bump)
- `fix:` bug fix (patch bump)
- `feat!:` or a `BREAKING CHANGE:` footer (major bump)
- `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else

## Changing signing

Signatures are a contract with every receiver. A change to `WebhookSigner`, `WebhookVerifier` or the payload handling:

1. must keep the reference test vector in `SigningTests` passing (it is shared with the Standard Webhooks libraries);
2. regenerates the cross-language vectors and reviews the diff like code:
   `dotnet run --project src/WebhookRelay.Server -- --emit-vectors "$PWD/samples/vectors/signature-vectors.json"`;
3. keeps the C#, Node and Python samples green (CI runs them against vectors generated in the same job).

## Changing delivery semantics

Retry, breaker and lease behaviour are what the delivery guarantees rest on. Changes need a test in
`tests/WebhookRelay.IntegrationTests` that shows the behaviour from the receiver's side (ids processed, duplicates),
not only from the relay's own state, and the crash test must stay green.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one; do not edit the old ADR in
place. Use the existing files as the format (MADR: context, options considered, decision, consequences).

## Database migrations

Migrations are numbered SQL files in `src/WebhookRelay.Server/Persistence/Migrations/` (`NNNN_name.sql`), embedded in
the binary and applied in order by `--migrate-only` as the schema owner. An applied migration must never change (its
SHA-256 is checked); add a new file instead. Keep the least-privilege grants in mind: the application role must not
gain UPDATE or DELETE on the attempt log.

## Benchmarks

Numbers in the README and `docs/benchmarks.md` are measured, never estimated. If a change affects performance, rerun
the relevant scenario (`dotnet run -c Release --project benchmarks/WebhookRelay.Benchmarks -- <scenario>`), commit the
JSON from `benchmark-results/`, and update the docs with the environment and host load.
