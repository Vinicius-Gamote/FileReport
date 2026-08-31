# FileReport — Requirements

Status: Comparison pipeline implemented; complete acceptance and a validated operating envelope remain pending.  
Language: English for code, API contracts, UI, messages, tests, and documentation.  
Related specifications: [System design](design.md) and [Implementation tasks](tasks.md).

## 1. Purpose and scope

FileReport will compare large CSV files, analyze their data quality, and present asynchronous results in an authenticated dashboard. Every evaluated workload must document its input volume, memory consumption, elapsed time, failures, and cost assumptions. There is no throughput, maximum supported file size, latency, memory, availability, or cost guarantee before measurement.

Repository organization: retain source, real tests, configuration, and SDD documents. Use native .NET/npm/Docker commands without a repository helper-script directory. Following the requested cleanup, dedicated benchmark tooling/workflows, generated datasets/results, test-result directories, and CI artifact files are not maintained in the source tree. Tests report through console/CI logs. Runtime metrics and the evidence requirements below remain applicable. Local service configuration is consolidated in the root Compose file and `config`; Compose reads `.env`, while native .NET development uses shared User Secrets and environment variables.

### Initial product decisions

- A comparison uses exactly two files: **Baseline** and **Candidate**, selected through the same Angular upload workflow. Each file receives its own validation and metrics. Standalone, single-file analysis is outside the first release.
- Users select one or more columns as a composite key. Comparison is independent of row order. Positional matching and automatic key inference are outside the first release.
- CSV bytes and full result artifacts live in private file storage. PostgreSQL stores accounts, file metadata, comparison configuration, job state, metrics, bounded result samples, and delivery records. Importing every CSV cell into permanent relational tables is outside the first release.
- The initial application has one API Gateway instance and one or more separately deployed workers. Local Docker Compose uses a shared file volume. Storage and SignalR scale-out must be revisited before deploying across hosts.
- Chart.js is the initial chart library; ngx-charts is an allowed replacement through a documented design change, not an additional dependency.
- These choices make unspecified product behavior explicit. Changes must update all affected requirements, design sections, and tasks.

## 2. Required technology and structure

| Area | Required choice |
| --- | --- |
| API | C#, a supported .NET LTS release, ASP.NET Core Minimal APIs, JWT bearer authentication, SignalR |
| Backend architecture | DDD with Domain, Application, Infrastructure, and host boundaries; thin endpoint handlers |
| Persistence | PostgreSQL, Entity Framework Core, compatible Npgsql provider, versioned migrations |
| Messaging | RabbitMQ producer in the API infrastructure, a separate .NET consumer worker, durable processing queue, dead-letter exchange and DLQ |
| Frontend | Angular, TypeScript, Angular Material, Chart.js, SignalR JavaScript client |
| Files | Private streamed storage through an abstraction; persistent shared Docker volume initially |
| Email | Resend, invoked only from the backend following an explicit **Send by email** action |
| Tests | .NET unit tests, Angular unit/component tests, integration tests against real infrastructure, end-to-end checks, reproducible benchmarks |
| Observability | Structured logs, OpenTelemetry instrumentation, runtime/container resource measurements, persisted job metrics |
| Delivery | GitHub Actions CI; Dockerfiles for API, worker, and frontend; `docker-compose.yml`; future deployment documentation |

Pin compatible stable versions and container images when scaffolding. Record the chosen versions and support status; do not use floating `latest` tags or describe this specification as a dependency compatibility test.

## 3. Functional requirements

Requirement identifiers are stable and referenced by `tasks.md`. Acceptance criteria describe observable behavior, not implementation completion.

### FR-001 — Registration, login, and identity

The system shall allow registration and login with email and password, issue a short-lived JWT, and authenticate HTTP and SignalR requests. Account creation shall **not** require an email confirmation message or verification link. Passwords shall use the ASP.NET Core Identity password hasher; plaintext passwords shall never be stored or logged.

Acceptance criteria:

- A newly registered account can log in without email confirmation; registration sends no email.
- Invalid credentials receive a generic error. Expired, incorrectly signed, wrong-issuer, and wrong-audience tokens are rejected.
- The browser keeps access tokens in memory. Reload or expiry requires login again in the first release; logout clears local identity and closes SignalR. Immediate server-side JWT revocation and refresh tokens are deferred.
- Registration/login rate limits and password policy are configured and tested. An email address on an account is explicitly treated as unverified.

### FR-002 — User isolation

The system shall authorize every job, upload, report, result download, email request, and hub subscription against the authenticated owner.

Acceptance criteria:

- One user cannot read, modify, subscribe to, download, or email another user's resources by changing an identifier.
- Owner identity comes from validated claims, never from a client-supplied `userId`, group name, or storage path. Unauthorized resource lookups do not disclose existence.
- The worker, storage, database, and broker are not public browser endpoints.

### FR-003 — Job creation and CSV upload

The system shall create a draft job before transferring files and accept one baseline file and one candidate file through authenticated HTTP streaming.

Acceptance criteria:

- The user sees named file slots, size validation, upload progress, and actionable errors. The frontend does not parse or copy the complete files into JavaScript memory.
- Server-side limits cover bytes, concurrent uploads, total user storage, upload duration, multipart headers, and file slots. Limits do not depend only on the filename, MIME type, or client checks.
- Partial, cancelled, and failed uploads are not eligible for processing. Retrying a slot before submission cannot produce multiple active file versions or overwrite another user's data.
- A job becomes `Ready` only when both immutable file references are committed. It cannot start with one missing file. A byte count and SHA-256 checksum are recorded per completed file.

### FR-004 — CSV interpretation and validation

The system shall use a streaming CSV parser with an explicit, versioned format contract.

Acceptance criteria:

- Each file is UTF-8, with an optional BOM and a required header. Comma is the default delimiter; semicolon or tab can be selected per file. Quotes, escaped quotes, embedded delimiters, quoted multiline fields, and LF/CRLF record endings are handled.
- Header names are nonempty and unique, case-sensitive, and not trimmed. Both files must have the same set of headers; column order may differ. Selected key and comparison columns must exist in both.
- Values are compared as decoded strings, using ordinal, case-sensitive equality with no implicit trimming, numeric/date conversion, Unicode normalization, or null inference. An empty quoted or unquoted field is an empty string; `NULL` is literal text.
- Zero-byte files, malformed records, invalid UTF-8, missing fields, extra fields, empty key components, duplicate keys, and configured record/field limits produce explicit validation errors. Blank records are validated rather than silently skipped; a final record terminator alone does not add a record. A header-only file is valid and contains zero data records.
- Invalid data prevents a successful comparison. Fatal parse errors may stop scanning; counts and diagnostics then disclose their partial scope. The system never silently drops invalid rows to produce an apparently complete comparison.

### FR-005 — Deterministic comparison

The system shall compare records using a user-selected key and report `Added`, `Removed`, `Changed`, and `Unchanged` counts. All non-key columns are compared by default; users may select a nonempty subset when non-key columns exist. Key-only schemas are valid and have no changed records.

Acceptance criteria:

- `Added` means a key exists only in Candidate; `Removed` means it exists only in Baseline. `Changed` means a shared key has at least one differing selected value. Other shared keys are `Unchanged`.
- Duplicate keys in either file fail validation even when their rows are identical. Composite keys cannot collide because of delimiter concatenation.
- A reordered file gives identical counts. Unselected value columns cannot change the classification. Selected keys/configuration become immutable on submission.
- For a successful comparison, `baselineRecords = Removed + Changed + Unchanged` and `candidateRecords = Added + Changed + Unchanged`. A difference between valid files is a successful result, not a processing failure.

### FR-006 — Asynchronous execution

The system shall persist a submission in PostgreSQL and reliably arrange its publication to RabbitMQ. A separate worker shall perform validation, comparison, and report creation outside the HTTP request.

Acceptance criteria:

- Submission returns `202 Accepted`, a job identifier, current state, and a status URL after committing the job and an outbox record; it does not wait for CSV processing.
- A temporary broker outage leaves a visible `PendingDispatch` job that can be published after recovery. No submission is reported as queued solely because an unconfirmed publish was attempted.
- Repeating submission with the same idempotency key and payload returns the same operation. Reusing the key for different content is rejected.
- RabbitMQ messages contain small versioned references, not CSV contents, credentials, or large report bodies.

### FR-007 — Retries, recovery, and DLQ

The system shall tolerate duplicate message delivery and worker restarts through durable job state, bounded retries, and idempotent result publication. It shall provide a DLQ for poison messages and exhausted processing failures.

Acceptance criteria:

- Retrying or redelivering a message does not duplicate final results, counters, or email deliveries. Only the current processing lease may commit results.
- Transient faults use a persisted retry schedule and a finite attempt budget. Crashes and expired leases also consume that budget. Deterministic CSV errors fail without infrastructure retries.
- Poison messages and exhausted processing faults are dead-lettered; known jobs expose a safe failure code. DLQ arrival is not confused with merely requesting a dead-letter operation.
- A broker interruption, unavailable DLQ, lost confirmation, crash before acknowledgment, and failed report commit each have a tested recovery path. There is no immediate infinite requeue loop or exactly-once delivery claim.
- DLQ inspection and replay are operator-only, audited operations. Replay does not silently reset limits or mutate completed results.

### FR-008 — SignalR from file selection

The frontend shall establish an authenticated SignalR connection when the user selects CSV files, subscribe to the newly created job, and receive server-side upload, state, and processing updates.

Acceptance criteria:

- Job subscription is authorized and acknowledged before uploads begin when the hub is available. Selecting a file initiates the connection; it does not send file bytes through SignalR.
- HTTP upload progress and server-received bytes have separate labels. An upload reaching 100% does not imply analysis completion.
- Events contain a job ID, monotonic job revision, stage, measured counters, timestamp, and safe status information. They contain no CSV rows or secrets.
- On reconnect the client reauthorizes, resubscribes, and fetches the HTTP snapshot. It ignores stale revisions. If SignalR is unavailable, the workflow continues with bounded HTTP polling and a disconnected indicator.

### FR-009 — Dashboard and history

The frontend shall display an Angular Material dashboard with Chart.js charts and owner-scoped job history.

Acceptance criteria:

- The dashboard shows job state, file metadata, comparison counts, per-file validation metrics, sampled errors, processing attempts, measured memory, timing, and cost status.
- Charts use aggregated data; result/error tables use capped pages and clearly disclose sample truncation. No chart requires downloading every input row.
- Empty, loading, disconnected, retrying, failed, expired-artifact, and successful states are distinct and accessible. Color is not the only status cue; charts have text/table equivalents.
- Missing values display `Not measured` or `Unavailable` with a reason, never a fabricated zero. Partial metrics are visibly marked. There is no estimated completion time without a measured basis.

### FR-010 — File and result retention

The system shall retain private source files and completed report artifacts according to configured policies and remove abandoned uploads and temporary processing files safely.

Acceptance criteria:

- Storage uses generated keys outside the web root; user filenames are display metadata only. Downloads go through owner-authorized endpoints.
- Completed jobs expose an immutable summary, bounded samples, and a streamed full difference artifact. Invalid jobs expose available diagnostics, not a complete comparison report.
- Retention does not delete files needed by an active upload, processing lease, retry, or download. Artifact expiry is visible without rewriting historical successful/failed outcomes.
- Local volumes survive application container recreation. Orphan recovery reconciles storage and PostgreSQL without claiming a transaction spans both systems.

### FR-011 — Explicit email report delivery

The dashboard shall offer **Send by email** for completed successful comparisons. An authenticated backend operation shall send a minimal report summary through Resend to the account email only.

Acceptance criteria:

- No result email is sent until the user clicks the button. Arbitrary recipient addresses are not accepted in the first release.
- The operation returns a delivery identifier and exposes `Pending`, `Sending`, `Accepted`, `Failed`, or `Unknown`. Provider acceptance is not presented as confirmed inbox delivery.
- Repeated clicks with the same request key and automatic retries share a stable Resend idempotency key. Provider uncertainty is retained; retries do not exceed the verified deduplication window.
- The email contains safe aggregate information and an authenticated report link, not source files, raw cell values, filenames, or credentials. Unverified recipient ownership remains a documented risk; per-user and global limits constrain abuse.
- Resend credentials are server-side secrets. A verified sender domain is deployment configuration and does not add account email confirmation.

### FR-012 — Measured processing volume and outcomes

The system shall record original input bytes and record counts separately from physical reads, repeated passes, and retries. It shall expose validation errors, infrastructure failures, retries, and dead-letter activity separately from CSV differences.

Acceptance criteria:

- Each attempt records bytes read, records parsed, stage, outcome, failure category/code, and completion or interruption time. Job-level totals state whether they include all attempts or only the successful one.
- Logical CSV records exclude the header and may span physical lines. Incomplete scans do not claim total input record counts.
- Successful counts reconcile according to FR-005. Failed/OOM/timeout runs remain in benchmark reports rather than disappearing from averages.

### FR-013 — Memory, time, and cost accounting

The system shall capture the metrics and calculation boundaries defined in `design.md`.

Acceptance criteria:

- Reports distinguish upload time, dispatch/queue delay, attempt processing time, retry wait, report persistence, and total submitted-job elapsed time.
- Memory measurements identify service, process/container scope, measurement tool, sampling interval, peak versus average, and incomplete observations. Managed heap and allocated bytes are not labeled as total memory.
- Costs identify compute, database, broker, file storage, temporary storage, network, observability, and email usage where applicable. Every estimate states currency, pricing date/source, rates, and shared-resource allocation assumptions.
- Missing prices or attribution yield an unavailable total or clearly labeled partial subtotal. Local runs are not called free; estimated cost is not called a provider bill.
- No claim about supported capacity or cost per job/GB is published without a linked, reproducible measurement and its limitations.

## 4. Nonfunctional requirements

| ID | Requirement | Acceptance evidence |
| --- | --- | --- |
| NFR-001 | Large-file processing shall use streamed I/O and bounded parser, sort, merge, sample, and message buffers. Concurrency, scratch space, output size, and storage quotas shall be configurable. | Review excludes whole-file materialization and unbounded key dictionaries; benchmarks record memory and scratch usage; limit and disk-full tests produce explicit failures. Bounded buffers are an implementation constraint, not an unmeasured memory guarantee. |
| NFR-002 | Production traffic shall use TLS; secrets shall remain out of source control, images, browser bundles, logs, URLs other than constrained hub token transport, and reports. | Security checks cover JWT validation, owner isolation, hub token-log redaction, restrictive CORS, rate limits, escaped output, private infrastructure, and least-privilege service accounts. |
| NFR-003 | Jobs and final results shall remain recoverable from process restarts and transient infrastructure faults. | Real-infrastructure tests demonstrate transactional outbox behavior, deduplication, lease fencing, retries, final-artifact consistency, and dead-letter handling. Recovery time is measured, not guaranteed. |
| NFR-004 | DDD boundaries shall separate business rules from EF Core, RabbitMQ, HTTP, SignalR, storage, and Resend. | Domain unit tests execute without infrastructure; architecture checks enforce dependency direction; endpoint and worker hosts delegate to application use cases. |
| NFR-005 | Diagnostic data shall support investigation without collecting raw CSV values or unbounded metric labels. | Correlated logs/traces connect API, outbox, broker, worker, and email; job IDs appear in logs/traces rather than time-series labels. Queue depth/age, failures, resource limits, pending outbox age, and DLQ depth are observable. |
| NFR-006 | Correctness and fault handling shall be tested at the appropriate layer. | Unit, integration, frontend, and end-to-end evidence covers the acceptance criteria; seeded fixtures define expected results; PostgreSQL/RabbitMQ tests do not rely on in-memory substitutes for infrastructure semantics. |
| NFR-007 | GitHub Actions shall validate changes and expose reproducible build/test outcomes. | PR CI restores locked dependencies, checks formatting/lint, builds, runs tests, checks migrations, builds all images, and retains outcomes in native job logs. It does not create or upload dedicated test-result/coverage artifacts. Small correctness smoke tests do not claim production capacity. |
| NFR-008 | Docker-based local operation and later deployment shall be documented. | Three Dockerfiles and Compose start frontend, API, worker, PostgreSQL, and RabbitMQ with health checks, persistent volumes, secrets configuration, and no committed credentials. Backup/restore, migrations, rollback, TLS, resource limits, storage, and SignalR scale-out are deployment checklist items. |
| NFR-009 | Benchmarks shall be reproducible and honest about uncertainty. | An opt-in matrix varies measured bytes/records, row width, differences, ordering, invalid input, and concurrency. Reports include environment, commit/configuration, repetitions, failures, memory, all timing boundaries, and cost status. Performance budgets are introduced only after reviewed measurements. |
| NFR-010 | The entire product shall use English. | UI labels, **Send by email**, API field names, status/error messages, code identifiers, test names, reports, logs, and documentation are English; original user filenames and CSV contents remain unchanged. |

## 5. Scope exclusions and remaining evidence

Initial exclusions: email-based account confirmation, automatic result emails, arbitrary email recipients, refresh tokens, password-recovery workflows, positional/fuzzy comparison, schema conversion, Excel/JSON imports, resumable/chunked uploads, permanent relational storage of every row, multi-host availability, and automatic production deployment.

Before implementation, select and record dependency versions, parser library, development quotas, timeout/lease settings, sampling settings, and retention periods. Before production, select hosting/storage providers, configure sender identity and secrets, review unverified-email abuse risk, exercise backup/restore, and collect capacity/cost evidence. These are implementation and release tasks, not reasons to invent benchmark results now.

## 6. Current measurement record

| Evidence | Current value |
| --- | --- |
| Executed comparison jobs | None; the end-to-end comparison pipeline is not implemented yet |
| Processed bytes and records | Not measured |
| API/worker/container memory | Not measured |
| Upload, queue, processing, and total elapsed time | Not measured |
| Observed failures and retry rates | Not measured |
| Per-job, per-GB, and environment cost | Not measured; no rates or provider selected |
| Validated operating envelope | Not established |

Implementation is complete only when acceptance evidence and any remaining limitations are recorded. The presence of infrastructure or an intended algorithm is not evidence of performance.
