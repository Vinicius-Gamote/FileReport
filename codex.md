# FileReport — Application Knowledge

Last synchronized with the specifications: 2026-08-31.  
Current stage: Comparison pipeline implemented; complete acceptance and production readiness remain under validation.

## 1. Purpose and source documents

This file consolidates application knowledge for contributors and coding agents. It is a project reference, not evidence that the described system has been implemented or tested.

| Source of truth | Contents |
| --- | --- |
| [Requirements](specs/requirements.md) | Product scope, required stack, 13 functional requirements, 10 nonfunctional requirements, and acceptance criteria |
| [System design](specs/design.md) | Architecture, domain boundaries, persistence, contracts, processing/recovery rules, security, and measurement methodology |
| [Implementation tasks](specs/tasks.md) | 22 tasks with completion status, dependencies, exit criteria, and requirement traceability |

Read the relevant source sections before implementation. This summary does not replace their detailed contracts or acceptance criteria. If it diverges from the specifications, reconcile it with those documents. Keep this file synchronized when accepted product or architectural decisions change.

## 2. Product objective and current status

FileReport will compare large CSV files, analyze their data quality, and display asynchronous results in an authenticated dashboard. A user uploads **Baseline** and **Candidate**, chooses comparison keys/options, follows processing, inspects results, and optionally clicks **Send by email**.

The entire product must use **English**: code identifiers, API fields, statuses, UI labels, error messages, tests, logs, reports, emails, and documentation. Preserve original user filenames and CSV contents.

The repository implements authentication, streamed uploads, EF Core persistence, outbox publication, RabbitMQ consumption, external comparison, report storage, SignalR updates, the Angular dashboard, explicit email delivery, and container/CI configuration. Local backend/frontend tests, real-infrastructure tests, container builds, and an API comparison smoke check have run. Full fault/recovery acceptance, browser end-to-end coverage, real email configuration, and remote CI verification remain incomplete. See [README.md](README.md) for setup, configuration, verification, and limitations. No supported capacity or performance guarantee has been established; missing measurements and prices must remain explicitly unavailable.

Repository organization follows the cleanup requested on 2026-08-30: keep application code, actual tests, configuration, SDD specifications, and this knowledge file. Essential Nginx/RabbitMQ configuration lives in `config`; the root Compose file contains all local services. Do not recreate dedicated benchmark, deployment, general documentation, helper-script, test-result, or CI-artifact directories. Dedicated benchmark tooling/workflows and generated datasets/results were removed; runtime measurement requirements remain. Use native `dotnet`, `npm`, and `docker compose` commands. Tests report through console/CI logs and do not persist result artifacts in the repository. Preserve ignored local secrets/data and useful dependency caches during routine cleanup.

Docker Compose reads root `.env`; `.env.example` deliberately leaves required secrets empty. Preserve existing database/broker credentials when migrating an environment. Native API/worker execution uses their shared .NET `UserSecretsId` in `Development`, with environment variables taking precedence. User Secrets are local development configuration, not encrypted production storage. Both native hosts must share an absolute `Storage:Root`; container hosts share the Compose volume instead. The existing local credentials were preserved in `.env` and the shared User Secrets store during the cleanup. Legacy `.local` configuration is retained as private data, not a required bootstrap mechanism. The deployed API smoke check belongs to the existing xUnit integration project and runs only when `FILEREPORT_SMOKE_BASE_URL` is explicitly set to an isolated stack with fake email.

Latest cleanup verification: direct builds/format checks passed, with 77 backend and 4 Angular tests passing. Six opt-in infrastructure/deployed-stack tests were skipped because Docker Desktop did not expose a working engine. API startup and `/health/live` succeeded with native User Secrets and background dispatchers disabled. Compose configuration and CI YAML syntax validated; the revised full-stack smoke check and remote CI still need execution. The existing Angular bundle-size warning remains.

## 3. Required stack and responsibilities

| Area | Planned technology and responsibility |
| --- | --- |
| Frontend | Angular, TypeScript, Angular Material, Chart.js, and the SignalR JavaScript client |
| API Gateway | C#, a supported .NET LTS release, ASP.NET Core Minimal APIs, JWT authentication, upload/query orchestration, RabbitMQ producer, and SignalR hub |
| Architecture | DDD with Domain, Application, Infrastructure, Contracts, and separate API/Worker hosts |
| Persistence | PostgreSQL through Entity Framework Core and a compatible Npgsql provider, with versioned migrations |
| Messaging | RabbitMQ durable processing queue, dead-letter exchange, and DLQ |
| Processing | Separate .NET Worker Service consuming comparison commands |
| File storage | Private streamed storage behind `IFileStore`; a shared persistent volume for the initial single-host topology |
| Email | Backend-only Resend integration following an explicit user action |
| Observability | Structured logs, OpenTelemetry, runtime/container measurements, and persisted job/attempt metrics |
| Validation | xUnit backend tests, Angular tests, real-infrastructure integration tests, end-to-end checks, and reproducible measurements outside the source tree |
| Delivery | GitHub Actions CI, separate Dockerfiles for API/worker/frontend, Docker Compose, and future deployment runbooks |

Chart.js is the initial chart choice. Replacing it with ngx-charts requires a documented design change; both are not required. The Gateway is an application entry point/BFF, not a requirement to add a separate proxy product. Pin supported, mutually compatible dependencies during T01; do not invent versions or use floating `latest` tags.

## 4. End-to-end workflow

1. The user registers or logs in. Registration requires no email confirmation and sends no email.
2. Selecting files starts an authenticated SignalR connection. The frontend creates a draft comparison and subscribes to its authorized job group before uploading when the hub is available.
3. Each side is uploaded through a separate authenticated HTTP streaming request. SignalR carries progress notifications, never file bytes. The browser holds file references rather than reading complete files into memory.
4. The Gateway streams to private storage, enforces limits, calculates actual bytes and SHA-256, finalizes the file, and commits metadata. Both committed file references are required for `Ready`.
5. The user selects key columns, compared columns, and per-file delimiters using bounded header previews. The worker will perform full validation.
6. Submission freezes files/options and commits `PendingDispatch` plus a RabbitMQ outbox command in one PostgreSQL transaction. HTTP returns `202 Accepted` with the job ID and status URL.
7. The Gateway outbox publisher sends a small versioned command to RabbitMQ. A worker claims an attempt, validates and compares the files, writes report artifacts, and persists results/metrics.
8. The worker writes notification outbox entries to PostgreSQL. A dispatcher inside the Gateway forwards them through its `IHubContext` to the browser. The dashboard fetches authoritative reports over HTTP.
9. Clicking **Send by email** creates a separate durable email delivery request. A Gateway background dispatcher invokes Resend; email failure does not change comparison success.

PostgreSQL is the authoritative source for job state. SignalR events are notifications and can be missed. Reconnection requires reauthorization, resubscription, and an HTTP snapshot; bounded polling provides fallback. Ignore stale event revisions. Upload transfer completion is not processing completion, and disconnecting does not cancel a submitted job.

## 5. CSV and comparison rules

The exact contract is defined by FR-004 and FR-005.

- Exactly two files participate in a comparison. Match rows by one or more user-selected key columns, independent of row order. Do not infer keys or perform positional matching.
- Accept UTF-8 with an optional initial BOM and a required header. Default to comma, with semicolon/tab selectable per file. Support quoted fields, escaped quotes, embedded delimiters, multiline fields, and LF/CRLF endings.
- Headers must be nonempty, unique, case-sensitive, and untrimmed. Both files require the same header set, but header order may differ.
- Compare decoded strings using ordinal, case-sensitive equality. Do not implicitly trim, normalize Unicode, convert numbers/dates, or infer nulls. Empty quoted/unquoted fields are empty strings; `NULL` is literal text.
- Keys must have nonempty components and be unique within each file. Duplicate keys fail validation even for identical rows. Composite key encoding must not collide through delimiter concatenation.
- Compare all non-key columns by default, or a selected nonempty subset when such columns exist. Key-only schemas are valid and cannot contain changed records.
- Zero-byte input, invalid UTF-8, malformed records, missing/extra fields, invalid keys, schema mismatch, and configured field/record limits produce explicit validation errors. Header-only files contain zero data records and are valid.
- Do not silently skip blank records or invalid data. A final record terminator alone creates no extra record. Fatal errors may stop a scan; remaining totals must then be marked unknown and diagnostics partial.

| Classification | Meaning |
| --- | --- |
| `Added` | Key exists only in Candidate |
| `Removed` | Key exists only in Baseline |
| `Changed` | Shared key with a difference in at least one selected comparison column |
| `Unchanged` | Shared key with identical selected values |

Successful results must satisfy:

```text
baselineRecords = Removed + Changed + Unchanged
candidateRecords = Added + Changed + Unchanged
```

Differences between valid files are successful comparison results, not processing failures. Invalid data cannot yield an apparently complete successful comparison.

## 6. Domain and storage boundaries

`ComparisonJob` is the comparison aggregate root. It controls ownership, file slots, submitted options, revisions, attempts, transitions, and the authoritative final report. `ComparisonOptions` and `CompositeKey` capture domain rules; `ProcessingAttempt` preserves execution history. Identity and email delivery have separate responsibilities.

Domain has no framework dependencies. Application depends on Domain and declares ports/use cases. Infrastructure implements persistence, CSV parsing, sorting/storage, RabbitMQ, email, and telemetry adapters. API/Worker compose them with thin transport handlers. Contracts expose versioned DTOs, not EF entities or domain aggregates.

PostgreSQL stores accounts, jobs, file metadata, attempts, reports, capped samples, artifacts' metadata, outbox entries, consumer receipts, email deliveries, and job measurement/cost records. Original CSV bytes and full result artifacts remain in private file storage; permanently importing every cell into relational tables is not in scope.

Use generated file keys outside the web root. Original filenames are display metadata, never paths. Record lengths/checksums, enforce ownership on downloads, and protect active uploads/attempts/retries/downloads during retention cleanup. Storage finalization and database commits are not one transaction: reconcile partial files, orphaned artifacts, and losing-attempt output safely. Artifact expiry must not rewrite a historical successful/failed job outcome.

Use UTC timestamps, large byte/record counters, explicit measurement units, decimal money, short database transactions, bounded queries, and unique constraints for idempotency. Preserve large-counter precision in JavaScript. Cap both sample counts and sample bytes; mark truncation.

## 7. Processing, states, and reliable messaging

The proposed algorithm is streaming parse → bounded external sort → bounded merge comparison. Spill sorted chunks to attempt-specific scratch files, limit merge fan-in/open handles, and detect duplicate keys across runs. Compare actual selected values, not hashes alone. Stream the full added/removed/changed artifact as versioned JSON Lines; unchanged records remain aggregate counts. Publish only after validation, invariants, and finalization succeed.

Never materialize whole files, retain a dictionary of every key, or use EF tracking as a dataset buffer. Bound parser fields/records, sort buffers, scratch/output space, samples, queues, and concurrency. These are design constraints, not proof of measured memory use.

The normal path is `Draft → Uploading → Ready → PendingDispatch → Queued → Processing → Succeeded`. Failures may produce `RetryScheduled` or `Failed`; abandoned unsubmitted jobs may become `Expired`. A fast consumer can move directly from `PendingDispatch`/`RetryScheduled` to `Processing` before the publisher records confirmation. Late updates must never regress state. See the full state machine in the design.

| RabbitMQ resource | Name / routing |
| --- | --- |
| Command exchange | `filereport.commands` |
| Processing queue | `filereport.comparisons.process`, key `comparison.requested.v1` |
| Dead-letter exchange | `filereport.deadletter` |
| DLQ | `filereport.comparisons.dlq`, key `comparison.failed.v1` |

- Use durable quorum queues, persistent messages, publisher confirms, mandatory routing checks, and manual acknowledgments. Explicitly configure/test at-least-once dead-lettering and its broker-version prerequisites. No exactly-once transport guarantee exists.
- Commands carry identifiers, versions, attempt numbers, and trace context. No CSV bodies, tokens, arbitrary file URLs, or large reports enter the queue.
- Keep republished command identity stable. A new processing attempt gets a new message ID. Persist delayed retries using `OutboxMessage.availableAt`; no additional delayed-message plugin is assumed.
- Lease each attempt with a fencing token. Only the current attempt/lease may commit progress or final results. Deduplication must not hide unfinished work, expired leases, or pending dead-letter intent.
- Acknowledge success only after durable result publication; acknowledge deterministic validation failures after persisting their outcome. Persist the next scheduled attempt before acknowledging a transient failure.
- Poison commands and exhausted processing faults go to the DLQ. Persist known-job failures and recover the crash window between dead-letter intent and rejection. Distinguish requested dead-lettering from observed DLQ arrival.
- Initial settings proposed in the design are one active job/prefetch one per worker and up to three processing attempts with 5/30-second delays plus bounded jitter. They remain configurable recovery safeguards, not measured capacity.
- Recovery accounts for crashes and expired leases in the finite attempt budget. Align broker acknowledgment and execution timeouts; avoid immediate infinite requeue loops. DLQ replay is audited and operator-only.

## 8. Authentication, dashboard, and email

Use ASP.NET Core Identity password hashing and short-lived JWTs with issuer/audience/signature/expiry validation. Account email confirmation is disabled. Access tokens remain in browser memory; reload/expiry requires login in the initial release. Logout clears local identity and SignalR but does not instantly revoke an already issued token.

Authorize every job, file, report, sample, download, email request, and hub subscription against validated owner claims. Never trust a client-supplied owner ID, group name, or storage path. Keep infrastructure private, restrict CORS, rate-limit exposed operations, escape displayed values, and use TLS in deployment. Redact secrets and constrained SignalR query-token transport from logs. Raw CSV values may appear only in authorized report data, not operational telemetry.

The dashboard uses Angular Material and Chart.js with aggregate charts and bounded tables. Display history, job/stage status, per-file quality, comparison counts, attempts, resource/time/cost metrics, samples, and artifact access. Distinguish loading, empty, disconnected, retrying, failed, successful, partial, and expired-artifact states. Provide text/table chart alternatives. Unknown metrics must not appear as zero or fabricated completion estimates.

**Send by email** is available for successful accessible reports. The backend sends a minimal aggregate summary and authenticated link to the account email only; omit filenames, raw cells, CSV attachments, and credentials. Registration and comparison completion send no automatic email. Recipient ownership remains unverified, so constrain sends and document the risk. Sender-domain verification is separate from account confirmation.

Email states are `Pending`, `Sending`, `Accepted`, `Failed`, and `Unknown`. Provider acceptance is not confirmed inbox delivery. Persist a stable provider idempotency key and payload; verify Resend's current deduplication window before implementation. Do not blindly retry uncertain outcomes after that window. Automated tests use a fake provider and send no real email.

## 9. Measurement and performance policy

Every evaluated workload must disclose **volume, memory, total time, failures, and cost status**. Record unavailable data explicitly rather than substituting zero. Keep design intent, observations, derived metrics, estimates, and incomplete measurements distinct.

| Dimension | Required interpretation |
| --- | --- |
| Volume | Per-side actual bytes/checksums and logical records excluding headers. Count unique input once; separately report physical I/O, repeated passes, and all retries. |
| Time | Separate upload, dispatch/queue delay, attempt/stage time, retry wait, and persistence. Submitted-job total is `terminalAt - submittedAt`, excluding upload and later email. Full workflow from first upload includes user delays and must be labeled separately. |
| Memory | Record API/worker process and container scope, managed heap separately, tool, sampling interval, baseline, observed peak, and gaps. Allocations are not live memory; unrelated service peaks cannot be summed into a simultaneous peak. |
| Failures | Preserve validation faults, infrastructure faults, crashes/OOM/timeouts, retries, redeliveries, final outcomes, and actual DLQ observations. Failed/skipped runs remain in benchmark evidence. |
| Cost | Include applicable compute, PostgreSQL, RabbitMQ, persistent/temporary storage, network, observability, and email. State currency, pricing source/date, billing units, rates, and shared-resource allocation. Missing components make a total unavailable or a subtotal explicitly partial. |

Use monotonic stage timers and disclose cross-process clock uncertainty. Concurrent jobs share process/container measurements; do not claim exact per-job memory without valid attribution. Correlate logs/traces by job/attempt/message but keep time-series labels low-cardinality. Cost estimates are not provider bills; local execution is not automatically free.

Benchmarks require fixture seeds/checksums, actual sizes/records, correctness checks, commit/configuration, environment/versions/resource limits, raw observations, warm-up separation, repeated runs where feasible, and limitations. Candidate large-file sizes and concurrency in the design are experiments, not supported capacity promises. Establish budgets or an operating envelope only after reviewed measurements.

## 10. Implementation roadmap and working rules

Follow the exact dependency graph and completion criteria in `specs/tasks.md`; numeric order alone is insufficient. For example, T18 depends on T19, and an infrastructure-only Compose setup may be introduced earlier for integration tests.

| Tasks | Work |
| --- | --- |
| T01–T05 | Pin toolchain/configuration; scaffold layers/tests; implement domain, persistence/migrations, and identity/ownership |
| T06–T08 | Private streamed uploads, HTTP job submission, transactional outbox, and RabbitMQ topology/publisher |
| T09–T13 | CSV validation, external comparison, worker/recovery, reports/retention, and measurements/cost accounting |
| T14–T17 | SignalR, Angular authentication/upload, dashboard, and explicit Resend delivery |
| T18–T20 | Cross-component/security tests, containerized topology, and GitHub Actions CI, in dependency order |
| T21–T22 | Reproducible benchmarks, runbooks, future deployment planning, and acceptance review |

Use the backlog's unchecked criteria to identify remaining acceptance work; do not equate implemented adapters with completed validation. Dependency versions are recorded in `global.json`, `Directory.Packages.props`, package manifests/lockfiles, and Dockerfiles. Safeguards live in `config/processing.defaults.json`. Real database/broker tests and local container execution have been exercised, but no cloud provider, price, or production environment has been selected. Dedicated benchmark automation is no longer maintained after the repository cleanup; any later measurement exercise must respect that organization.

Application code locations are `src/FileReport.Domain`, `src/FileReport.Application`, `src/FileReport.Infrastructure`, `src/FileReport.Contracts`, `src/FileReport.Api`, `src/FileReport.Worker`, and `web/filereport-ui`. Actual test projects live in `tests` and runtime configuration in `config`. The root README and CI workflow document direct build, migration, run, and test commands without repository command wrappers. Infrastructure tests use a separate database/file store so their pending messages cannot leak into the deployed smoke workflow.

Tests should demonstrate business rules, ownership, parser edge cases, count invariants, bounded handling, transaction/lease recovery, duplicate messages, DLQ behavior, reconnect, retention, and ambiguous email outcomes. Use real PostgreSQL/RabbitMQ for their semantics, plus unit/component/end-to-end tests at the appropriate boundaries. CI must preserve test/coverage evidence; small smoke workloads do not prove production performance.

For completed work, record task and requirement IDs, changed files/migrations, verification commands/environment, observed results/artifact paths, measurements or reasons they are missing, limitations, and specification changes. Do not mark a task complete from scaffolding alone. Keep secrets and large generated datasets out of source control.

## 11. Initial exclusions and deployment boundaries

The initial scope excludes standalone single-file analysis, positional/fuzzy matching, automatic key inference, schema/type conversion, Excel/JSON imports, permanent relational storage of all CSV rows, resumable/chunked uploads, account confirmation emails, refresh tokens, password recovery, automatic result emails, arbitrary email recipients, and automatic production deployment.

The first topology has one Gateway and separately deployed workers sharing files on one host. Local container persistence is not multi-host availability. Before multiple Gateway instances, design and test a SignalR backplane/managed service and load-balancer behavior; database polling alone is insufficient. Multi-host processing also requires suitable durable shared/object storage.

Future deployment requires TLS/DNS, secrets and rotation, sender configuration, migration/rollback planning, tested database/file backups and restores, broker recovery, retention, alerts, measured capacity, cost review, and documented unverified-email risk. These remain planned work, not completed infrastructure or deployment authorization.
