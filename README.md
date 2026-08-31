# FileReport

Compare two CSV files through an Angular dashboard, a .NET Minimal API Gateway, and an asynchronous RabbitMQ worker. Code, UI, and documentation use English. Product rules and acceptance criteria live in [specs](specs/requirements.md); [codex.md](codex.md) consolidates application knowledge.

## Application

- JWT registration/login without account email confirmation, and ownership checks on jobs, files, reports, and SignalR subscriptions.
- Streamed Baseline/Candidate uploads to private storage, with PostgreSQL metadata and EF Core migrations.
- Explicit comparison keys, strict CSV validation, bounded external sorting, and Added/Removed/Changed/Unchanged results.
- Transactional outbox, RabbitMQ processing queue and DLQ, finite retries, processing leases, and a separate worker.
- Angular Material, Chart.js, SignalR progress, HTTP snapshot recovery, history, report downloads, and explicit **Send by email** through a backend Resend adapter.
- Persisted processing measurements, explicit cost availability, separate application Dockerfiles, and GitHub Actions validation.

The pipeline is implemented, but the complete acceptance checklist remains open. Comprehensive failure/recovery testing, browser end-to-end coverage, real email configuration, and production readiness still require validation. CI configuration is present; a successful remote GitHub run has not been established. Automated tests and local examples use fake email.

The command-wrapper removal was checked with direct backend/frontend builds, formatting, 77 backend tests, and 4 Angular tests. Six opt-in infrastructure/deployed-stack tests were skipped because the local Docker engine did not become available. Native API startup and liveness were verified using User Secrets with background dispatchers disabled; this does not prove database/broker readiness. Compose configuration and CI YAML syntax validated. Angular still reports its existing initial-bundle size warning.

## Prerequisites

- Docker with Compose for the complete application or PostgreSQL/RabbitMQ alone.
- For development outside containers: .NET SDK compatible with [global.json](global.json), Node.js 24.18.0, and npm 11.16.0.
- The command examples below use PowerShell 7; CI uses equivalent native commands on Linux. No repository command wrappers are required.

NuGet versions are centralized in [Directory.Packages.props](Directory.Packages.props). NuGet/npm lockfiles and container image digests pin dependency graphs. Validate changes to those pins.

## Run the complete application

Create a root `.env` from [.env.example](.env.example) on first setup. Do not overwrite an existing `.env`. Fill `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, and `JWT_SIGNING_KEY` with distinct random values of at least 32 bytes. Existing PostgreSQL/RabbitMQ volumes require their original passwords. A 32-byte random value can be generated locally with `[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`.

```powershell
docker compose up -d --build --wait
```

Compose reads `.env` automatically and starts PostgreSQL, RabbitMQ, storage initialization, migration, API, worker, and frontend. Open [FileReport](http://localhost:8080); the API also binds to `http://127.0.0.1:5080`. Infrastructure ports bind to loopback. No .NET/npm installation is needed for this container workflow.

```powershell
docker compose down
```

Ordinary `down` retains named volumes. Do not remove volumes or `.local` during routine cleanup: they may contain persistent files or legacy private configuration. Existing `.local/dev.env` can still be used with `docker compose --env-file .local/dev.env ...`, but it is not required; use the same credentials when adopting root `.env`.

## Restore, build, and test directly

From the repository root:

```powershell
dotnet restore FileReport.slnx --locked-mode --configfile NuGet.Config
dotnet build FileReport.slnx --no-restore --configuration Release
$testResults = Join-Path ([IO.Path]::GetTempPath()) 'FileReport/test-results'
dotnet test FileReport.slnx --no-build --no-restore --configuration Release --logger trx --results-directory $testResults
dotnet format whitespace FileReport.slnx --no-restore --verify-no-changes
npm ci --prefix web/filereport-ui
npm --prefix web/filereport-ui run format:check
npm --prefix web/filereport-ui run build
npm --prefix web/filereport-ui test
```

Infrastructure and deployed-stack tests are opt-in. The default suite skips them instead of pretending external dependencies were exercised. Use `--results-directory` to keep generated evidence in temporary storage; CI uploads TRX files from its runner's temporary directory. Tool caches, installed dependencies, and generated build outputs are ignored by Git.

If Windows `npm` resolves to a broken global shim, use the npm CLI bundled with Node directly, without changing the repository or your global installation:

```powershell
$npmCli = Join-Path (Split-Path (Get-Command node).Source) 'node_modules/npm/bin/npm-cli.js'
node $npmCli --prefix web/filereport-ui run build
node $npmCli --prefix web/filereport-ui test
```

The same CLI accepts `ci`, `start`, and formatting commands. An existing `.cache/nuget` can be reused by setting `NUGET_PACKAGES` to its absolute path before restore/build; this is optional, not a runtime dependency.

## Develop API and worker outside containers

Use this topology instead of containerized API/worker/web. They share ports but use different file stores, so running both modes against the same jobs is unsafe. Stop existing application containers first, without deleting volumes.

Both .NET hosts share a `UserSecretsId`. Configure the following once from the repository root, using the database/broker passwords already configured in `.env`:

```powershell
$databasePassword = Read-Host 'PostgreSQL password from .env' -MaskInput
$brokerPassword = Read-Host 'RabbitMQ password from .env' -MaskInput
$signingKey = Read-Host 'JWT signing key from .env' -MaskInput
dotnet user-secrets set 'ConnectionStrings:Database' "Host=127.0.0.1;Port=55432;Database=filereport;Username=filereport;Password=$databasePassword" --project src/FileReport.Api
dotnet user-secrets set 'RabbitMq:Host' '127.0.0.1' --project src/FileReport.Api
dotnet user-secrets set 'RabbitMq:Port' '55672' --project src/FileReport.Api
dotnet user-secrets set 'RabbitMq:User' 'filereport' --project src/FileReport.Api
dotnet user-secrets set 'RabbitMq:Password' $brokerPassword --project src/FileReport.Api
dotnet user-secrets set 'Jwt:Issuer' 'FileReport' --project src/FileReport.Api
dotnet user-secrets set 'Jwt:Audience' 'FileReport.Web' --project src/FileReport.Api
dotnet user-secrets set 'Jwt:SigningKey' $signingKey --project src/FileReport.Api
dotnet user-secrets set 'Storage:Root' (Join-Path (Get-Location) '.local/storage') --project src/FileReport.Api
dotnet user-secrets set 'Email:Mode' 'Fake' --project src/FileReport.Api
dotnet user-secrets set 'Email:From' 'FileReport <reports@example.test>' --project src/FileReport.Api
dotnet user-secrets set 'Email:ReportBaseUrl' 'http://localhost:4200' --project src/FileReport.Api
dotnet user-secrets set 'Cors:Origin' 'http://localhost:4200' --project src/FileReport.Api
```

User Secrets are local development configuration, not encrypted production storage. Both default .NET hosts load them in the `Development` environment; environment variables override them. Host processes do not load Docker's `.env` automatically. See [Microsoft's User Secrets guidance](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0).

```powershell
docker compose up -d --wait postgres rabbitmq
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/FileReport.Api -- --migrate
dotnet run --project src/FileReport.Api -- --urls http://127.0.0.1:5080
```

In a second terminal:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/FileReport.Worker
```

In a third terminal:

```powershell
npm --prefix web/filereport-ui start
```

Open [the Angular development server](http://localhost:4200). Stop each host with Ctrl+C. Both native hosts use the same absolute file-store path from User Secrets. Container processes use the shared `files` volume; existing jobs cannot transparently move between these file stores.

## Tests against running dependencies

Run infrastructure tests only against an isolated database and file store with fake email. Configure the shared development secrets above, start PostgreSQL/RabbitMQ, and create the test database once:

```powershell
docker compose exec -T postgres createdb -U filereport filereport_tests
$env:DOTNET_ENVIRONMENT = 'Development'
$databasePassword = Read-Host 'PostgreSQL password from .env' -MaskInput
$env:ConnectionStrings__Database = "Host=127.0.0.1;Port=55432;Database=filereport_tests;Username=filereport;Password=$databasePassword"
$env:Storage__Root = Join-Path ([IO.Path]::GetTempPath()) 'FileReport/infrastructure-storage'
$env:Email__Mode = 'Fake'
dotnet run --project src/FileReport.Api -- --migrate
$env:RUN_INFRASTRUCTURE_TESTS = '1'
dotnet test tests/FileReport.IntegrationTests --filter FullyQualifiedName~InfrastructureTests --logger trx --results-directory (Join-Path ([IO.Path]::GetTempPath()) 'FileReport/test-results')
```

Use a separate terminal for these overrides and close it afterward. Do not start application hosts in that test-configured terminal.

The deployed API smoke check is a normal xUnit test, invoked against a running isolated full stack:

```powershell
$env:FILEREPORT_SMOKE_BASE_URL = 'http://127.0.0.1:5080'
dotnet test tests/FileReport.IntegrationTests --filter FullyQualifiedName~DeployedApiTests --logger trx --results-directory (Join-Path ([IO.Path]::GetTempPath()) 'FileReport/test-results')
```

It creates synthetic accounts and files, requires fake email before sending any email request, verifies all four result counts, downloads the artifact, checks cross-user isolation, and verifies email idempotency. Job/report diagnostics are captured in the test output. There is no separate smoke runner file.

## Configuration and repository map

[processing.defaults.json](config/processing.defaults.json) defines upload, parser, sort, scratch, retry, lease, sampling, and retention safeguards. Environment variables such as `Processing__MaxFileBytes` override defaults. These are constraints to evaluate, not proven capacity or process-memory guarantees.

| Location | Purpose |
| --- | --- |
| `src/FileReport.Domain` | Framework-independent comparison rules and lifecycle |
| `src/FileReport.Application` | Use cases, ports, configuration, and cost accounting |
| `src/FileReport.Infrastructure` | EF persistence, storage, CSV processing, RabbitMQ, email, and telemetry |
| `src/FileReport.Contracts` | HTTP contracts |
| `src/FileReport.Api`, `src/FileReport.Worker` | Separate hosts, shared development secrets ID, and Dockerfiles |
| `web/filereport-ui` | Angular/Material dashboard, tests, npm commands, and Dockerfile |
| `tests` | Domain, application, architecture, parser, API, infrastructure, and deployed API tests |
| `config` | Processing defaults, Nginx, and RabbitMQ configuration |
| `specs`, `codex.md` | SDD documents and application knowledge |
| `.github/workflows/ci.yml` | Direct .NET/npm/Docker commands and temporary test evidence |
| `docker-compose.yml`, `.env.example` | Local topology and required environment configuration |

## Security and measurement policy

Never commit `.env`, User Secrets, local files, or test datasets. Registration and job completion send no automatic email. Real Resend use requires backend credentials and a verified sender; provider acceptance does not prove inbox delivery. Account email ownership remains unverified.

No public deployment is configured. Production requires TLS, secret management, tested backups/restores, recovery, storage planning, abuse-risk review, and measured operating limits. A local single-host topology does not establish multi-host availability.

Reports must distinguish measured volume, memory, elapsed time, failures, and cost status from unavailable observations. Missing prices produce unavailable cost, not zero. No throughput, latency, memory, maximum supported file size, or price guarantee has been established. Dedicated benchmark tooling/datasets/results are not maintained in this repository; runtime metrics and reproducible evidence requirements remain. [tasks.md](specs/tasks.md) tracks acceptance work.
