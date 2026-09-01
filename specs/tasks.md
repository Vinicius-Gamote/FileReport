# FileReport — Implementation Tasks

Status: T01–T03 completed; implementation now spans the comparison pipeline. Unchecked criteria below still require acceptance review and must not be treated as complete from code presence alone.  
Inputs: [Requirements](requirements.md) and [System design](design.md).  
Language: Implement the entire application and its documentation in English.

Current capabilities, local commands, and validation limitations: [README](../README.md). Repository cleanup removed the root backend `tests` directory, `.cache`, dedicated benchmark tooling/workflows, generated results, placeholder directories, and the command-wrapper directory. Do not recreate those paths without an explicit scope change. Angular workspace caching is disabled; colocated Angular specifications remain. CI now validates formatting, builds, Compose, images, and the remaining frontend specifications. Backend correctness, recovery, infrastructure, and end-to-end test tasks below are superseded by the current scope and remain unmet rather than being inferred from code presence. Essential service configuration is in `config` and the root Compose file, with project knowledge in README, codex.md, and these specifications. Native development uses shared .NET User Secrets; Compose reads root `.env`.

## 1. Execution rules

- Read the relevant requirement acceptance criteria and design sections before implementing a task. These three specifications are the initial deliverable; they do not imply application code, tests, infrastructure, or benchmarks already exist.
- Follow the dependency graph. Task numbers are identifiers, not a requirement to delay an independent prerequisite such as the local container setup.
- Implement the smallest complete behavior satisfying the mapped requirements. Under the current no-backend-tests scope, record backend correctness and PostgreSQL/RabbitMQ semantics as unverified rather than inferring them from compilation or mocks.
- Keep Minimal API handlers and worker adapters thin. Preserve Domain/Application/Infrastructure boundaries and owner checks.
- Record chosen versions, configuration defaults, tradeoffs, validation commands/results, and any deviations. Update the specifications when a change alters observable behavior or architecture.
- Never mark a task complete because scaffolding exists or a dependency starts. Check its exit criteria and record evidence. Use `Not measured` for missing metrics; retain failed and skipped benchmark cases.
- Do not publish a throughput, latency, memory, capacity, or price claim until a reproducible measurement supports it. Initial limits are safeguards to evaluate, not advertised capacity.
- Do not send real email during local or CI validation, commit secrets or large datasets, or deploy to a paid/public environment as part of local implementation work.

## 2. Task backlog

### T01 — Pin the toolchain and initial configuration

- [x] Select compatible supported .NET, EF Core/Npgsql, PostgreSQL, RabbitMQ/client, Angular/Material, Chart.js, SignalR client, CSV parser, build tooling, and container versions.
- [x] Record versions/support checks and pin SDKs, dependency lockfiles, and image tags/digests. Verify the parser's streaming, multiline, limit, and error-location behavior.
- [x] Define documented development defaults for upload/file/record/field limits, per-user storage, concurrency, scratch/output quotas, execution/broker timeouts, leases, retention, retries, and metric sampling. Keep provider prices unavailable until sourced.

Dependencies: None.  
Requirements: FR-004, FR-013, NFR-001, NFR-007, NFR-008.  
Done when: Toolchain choices and every initial limit have an explicit rationale and configuration location; no default is represented as measured capacity.

### T02 — Scaffold hosts and layers

- [x] Create the backend solution, Domain/Application/Infrastructure/Contracts projects, Minimal API Gateway, separate Worker Service, and Angular workspace described in `design.md`.
- [x] Configure dependency injection, formatting, nullable/type checks, structured configuration, and project references for dependency direction.
- [x] Document direct local build and frontend-check commands in the development README; keep names, messages, and comments in English. Do not require repository command wrappers.

Dependencies: T01.  
Requirements: NFR-004, NFR-006, NFR-010.  
Done when: Both hosts and the frontend build from documented commands. Dependency direction remains reviewable, but no executable architecture check exists in the current scope.

### T03 — Implement the comparison domain and state rules

- [x] Model comparison jobs, file slots, options, composite keys, attempts, result classifications, and lifecycle transitions.
- [x] Implement immutable submitted configuration, unique-key semantics, exact string comparison policy, count invariants, revisions, and terminal-result rules.
- [ ] Automated domain verification for reordered inputs, key-only schemas, selected columns, duplicate/empty keys, invalid transitions, and result classification was removed with the backend test projects.

Dependencies: T02.  
Requirements: FR-004, FR-005, FR-006, FR-007, NFR-004, NFR-006.  
Done when: Domain behavior is implemented. Its observable semantics remain without executable verification under the current scope.

### T04 — Implement PostgreSQL persistence and migrations

- [ ] Map accounts, jobs, stored files, attempts, reports, samples, outbox entries, consumer receipts, email deliveries, and measurement/cost records with EF Core/Npgsql.
- [ ] Add keys, uniqueness constraints, revisions/fencing fields, indexes, UTC timestamps, large counters, and bounded read models.
- [ ] Add migrations for schema creation and representative upgrades. Record concurrent-claim, idempotency-conflict, and transaction-rollback behavior as unverified under the current scope.

Dependencies: T03.  
Requirements: FR-001, FR-002, FR-006, FR-007, FR-010, FR-013, NFR-003, NFR-006.  
Done when: Migrations and constraints are implemented and no long-lived CSV row tracking is introduced. Atomic job/outbox behavior against real PostgreSQL remains an explicit validation gap.

### T05 — Implement registration, JWT login, and ownership

- [ ] Configure ASP.NET Core Identity password hashing with email confirmation disabled; implement registration/login/current-user endpoints without sending email.
- [ ] Validate JWT issuer, audience, signature, expiry, and signing configuration. Configure password policy, generic login errors, and authentication rate limits.
- [ ] Centralize owner authorization for jobs, files, results, and deliveries. Record forged-owner, expired-token, and cross-user access scenarios as unverified backend gaps.

Dependencies: T04.  
Requirements: FR-001, FR-002, NFR-002, NFR-006.  
Done when: A registered user can log in immediately, invalid tokens fail, and owner checks cannot be bypassed through transport parameters. No account confirmation email is generated.

### T06 — Implement private storage and streamed uploads

- [ ] Implement `IFileStore` using a private persistent volume, generated keys, temporary/final file states, checksums, and bounded asynchronous I/O.
- [ ] Implement draft file-slot reservation and authenticated single-file streaming endpoints with server-side limits and generation/revision checks.
- [ ] Add bounded header previews. Record interrupted transfer, excess size, invalid filename/path traversal, concurrent replacement, partial write, failed commit, and retry scenarios as unverified backend gaps.

Dependencies: T04, T05.  
Requirements: FR-002, FR-003, FR-004, FR-010, NFR-001, NFR-002, NFR-003.  
Done when: Two complete immutable file references are required for readiness; no incomplete upload can be processed; original names never control filesystem paths; large uploads are not materialized in memory.

### T07 — Implement job submission and HTTP contracts

- [ ] Implement create/options/submit/status/history use cases and Minimal API endpoints, including owner scoping and paginated history.
- [ ] Persist job state and a versioned comparison command in one transaction. Add request-hash idempotency and reject stale state/preconditions.
- [ ] Publish OpenAPI contracts, Problem Details error codes, metric availability fields, and examples of successful/conflicting/invalid requests.

Dependencies: T03, T04, T06.  
Requirements: FR-002, FR-005, FR-006, FR-009, NFR-004, NFR-010.  
Done when: Submission returns `202` after durable acceptance, never waits for comparison, and repeated identical requests cannot create duplicate operations.

### T08 — Configure RabbitMQ topology and the outbox publisher

- [ ] Declare the command exchange, quorum processing queue, dead-letter exchange, and quorum DLQ with version-compatible dead-letter, overflow, delivery-limit, and size policies.
- [ ] Implement leased outbox batches, delayed availability, persistent messages, publisher confirms, mandatory-return handling, and backoff for unavailable infrastructure.
- [ ] Record broker outage/recovery, unrouteable command, lost confirmation, repeated publication, and fast-consumer race scenarios as unverified infrastructure gaps.

Dependencies: T04, T07.  
Requirements: FR-006, FR-007, NFR-003, NFR-005, NFR-006.  
Done when: A committed submission is recoverably dispatched and never falsely marked queued; no CSV bytes enter messages; DLQ prerequisites are exercised against the pinned broker.

### T09 — Implement the streaming CSV validation pipeline

- [x] Implement the UTF-8/BOM/header/delimiter/quote/string contract plus explicit bounded streaming transcoding from Windows-1252 and UTF-16; impose bounded field/record/column handling without silent encoding fallback.
- [ ] Count logical records and actual source bytes, preserve accurate available locations, and produce stable bounded diagnostics with completeness flags.
- [ ] Record quoted multiline fields, escaped delimiters/quotes, LF/CRLF, invalid UTF-8, whitespace/case, empty values, literal `NULL`, reordered headers, header-only input, zero-byte input, blank records versus a final terminator, and malformed rows as unverified parser cases.

Dependencies: T03, T06.  
Requirements: FR-004, FR-012, NFR-001, NFR-006.  
Done when: Fixtures prove the parser contract; fatal errors do not become successful partial comparisons, and field limits prevent a single record from bypassing buffer bounds.

### T10 — Implement external sort and merge comparison

- [ ] Build bounded sort chunks, length-prefixed composite key serialization, attempt-specific spill files, bounded merge fan-in, and selected-value comparison.
- [ ] Detect duplicate keys across all runs; stream full differences while retaining only capped samples/counters; enforce scratch and output quotas.
- [ ] Keep the simple trusted small-fixture oracle as a future validation design outside the current repository scope. Record multi-pass merging, key skew, cross-run duplicates, empty sides, reordered inputs, all differences, and failure cleanup as unverified.

Dependencies: T09.  
Requirements: FR-004, FR-005, FR-010, FR-012, NFR-001, NFR-006.  
Done when: The bounded algorithm is implemented; oracle agreement and explicit resource-limit behavior remain unverified. Actual resource behavior remains to be measured in T21.

### T11 — Implement the worker, retries, and crash recovery

- [ ] Consume versioned commands, claim attempts with leases/fencing, run validation/comparison, and commit only the current attempt's result.
- [ ] Implement manual acknowledgment after durable disposition, idempotent terminal handling, persisted finite retries, poison-message rejection, dead-letter intent recovery, and expired-lease reconciliation.
- [ ] Configure worker concurrency/prefetch and execution/broker acknowledgment timeouts; support graceful shutdown and bounded dependency backoff.
- [ ] Record duplicate delivery, database outage, worker kill, lease expiry, stale completion, post-commit crash, retry exhaustion, unavailable DLQ, and acknowledgment timeout as unverified fault scenarios.

Dependencies: T08, T10.  
Requirements: FR-006, FR-007, FR-012, NFR-001, NFR-003, NFR-006.  
Done when: Recovery preserves one authoritative result, accounts for failed/crashed attempts, terminates retries, and routes poison/exhausted work to the DLQ without a hot requeue loop.

### T12 — Implement report queries, artifacts, and retention

- [ ] Finalize checksummed immutable result artifacts and transactionally publish their metadata, count summaries, and bounded samples.
- [ ] Implement owner-authorized report/sample/download queries with page/byte limits, truncation/completeness fields, safe output encoding, and expired-artifact responses.
- [ ] Implement retention and orphan reconciliation for drafts, source files, reports, sort runs, and stale temporary objects with active-work/download protection.
- [ ] Record failed finalization/commit, losing-attempt artifacts, container restart, concurrent cleanup/download, and result-count invariants as unverified backend scenarios.

Dependencies: T06, T07, T11.  
Requirements: FR-002, FR-005, FR-009, FR-010, FR-012, NFR-001, NFR-003.  
Done when: A successful report never points to a partial artifact, failed results are visibly incomplete, and cleanup cannot delete another job's or active operation's files.

### T13 — Instrument resource use, elapsed time, failures, and cost

- [ ] Implement per-attempt/job counters and timing with the boundaries in `design.md`, distinguishing unique inputs from repeated reads and retries.
- [ ] Add structured logs, tracing, low-cardinality metrics, resource samplers, and external process/container observations that survive worker termination where possible.
- [ ] Add a versioned rate-card/cost estimator with unit checks, component breakdown, shared-cost allocation, currency, pricing provenance, and unavailable/partial statuses.
- [ ] Record timing/counter aggregation, retries, unknown totals/rates, zero denominators, metric gaps, overlapping stages, shared memory, and OOM incompleteness as unverified measurement scenarios.

Dependencies: T11, T12.  
Requirements: FR-012, FR-013, NFR-005, NFR-009.  
Done when: Reports can identify volume, memory scope, total time, failures, and cost provenance without fabricated values, double-counting unique input, or treating estimates as bills.

### T14 — Implement SignalR and reliable dashboard reconciliation

- [ ] Add the authenticated job hub, server-generated groups, subscription ownership checks, expiry behavior, and token-log redaction.
- [ ] Persist small versioned upload/worker notifications and dispatch them from the Gateway using its `IHubContext`; coalesce progress while preserving terminal updates.
- [ ] Implement subscription snapshots, concurrent event ordering, stale-revision handling, reconnect, reauthorization, and polling fallback. Record backend transport behavior without automated evidence.

Dependencies: T07, T11, T13.  
Requirements: FR-002, FR-008, FR-009, NFR-002, NFR-003.  
Done when: A worker in a separate process can update an authorized browser through the Gateway, missed events recover from PostgreSQL state, and another user's subscription is rejected.

### T15 — Build Angular authentication and file input

- [ ] Build an Angular Material shell, registration/login views, protected navigation, and JWT handling in memory with expiry/logout behavior.
- [ ] Add Baseline/Candidate file inputs, start SignalR on selection, create/subscribe to the draft, stream HTTP uploads, and distinguish browser transfer from server-received progress.
- [ ] Add bounded header previews, key/column/delimiter controls, readiness validation, upload retry, and submission feedback without reading full files in JavaScript.
- [ ] Cover form validation, immediate post-registration login, subscription-before-upload ordering, missing-file prevention, token expiry, and degraded hub operation in colocated frontend specifications where feasible.

Dependencies: T05, T06, T07, T14.  
Requirements: FR-001, FR-002, FR-003, FR-004, FR-005, FR-008, NFR-001, NFR-010.  
Done when: The English UI can submit a two-file comparison and follow it after login/reconnect without conflating upload completion with processing completion.

### T16 — Build dashboard, charts, and history

- [ ] Add owner job history, state/stage displays, per-file quality metrics, Chart.js comparison charts, and bounded sample tables.
- [ ] Add attempt history, memory/timing/volume panels, cost breakdown/provenance, artifact downloads, partial-data labels, and unknown-value states.
- [ ] Provide accessible chart alternatives and explicit loading, empty, retry, failure, disconnect, success, and artifact-expiry views.
- [ ] Cover count rendering, pagination/truncation, stale event rejection, failed attempts, missing metrics, and large counters in colocated frontend specifications where feasible.

Dependencies: T12, T13, T15.  
Requirements: FR-008, FR-009, FR-010, FR-012, FR-013, NFR-010.  
Done when: Dashboard values reconcile with API reports, charts use aggregates, and no chart or label suggests unmeasured capacity or a false zero.

### T17 — Add explicit Resend report delivery

- [ ] Implement the owner-authorized email request/status endpoints, durable delivery claims, safe summary template, and backend Resend adapter.
- [ ] Add **Send by email**, display the account recipient, enforce per-user/global send limits, and expose pending/accepted/failed/unknown states.
- [ ] Verify the current provider idempotency contract and configure a bounded retry/reconciliation window. Keep request key and provider payload stable through retries.
- [ ] Record double clicks, provider errors/timeouts, post-acceptance crashes, deduplication expiry, unauthorized access, and absence of automatic email as unverified backend/provider scenarios.

Dependencies: T05, T12, T16.  
Requirements: FR-001, FR-002, FR-011, FR-013, NFR-002, NFR-006.  
Done when: Only a deliberate click requests delivery, CI uses a fake provider, credentials stay server-side, and `Accepted` is never labeled `Delivered`. Sender-domain setup is documented separately from account confirmation.

### T18 — Record cross-component validation gaps

- [ ] Record that registration/login → uploads → RabbitMQ → worker → report → SignalR/polling → charts → explicit email lacks an automated cross-component check.
- [ ] Record owner-isolation, Problem Details, redaction, rate-limit, CORS, safe-rendering, and infrastructure-boundary gaps.
- [ ] Record unverified restart/fault scenarios across storage, PostgreSQL, RabbitMQ, Gateway, and worker, including leases, retries, dead letters, and duplicate effects.
- [ ] Do not recreate backend test projects or generated evidence directories without an explicit scope change.

Dependencies: T17, T19.  
Requirements: FR-001, FR-002, FR-007, FR-008, FR-010, FR-011, NFR-002, NFR-003, NFR-006.  
Done when: The missing cross-component evidence is explicit and no build-only result is presented as correctness or recovery proof.

### T19 — Containerize the applications and local infrastructure

- [ ] Create separate multi-stage Dockerfiles for Gateway, worker, and Angular static hosting with supported non-root execution and production build settings.
- [ ] Create `docker-compose.yml` with PostgreSQL, RabbitMQ/topology/DLQ, internal networking, dependency health checks, persistent database/broker/file volumes, scratch limits, and secret placeholders.
- [ ] Configure proxy streaming/body limits, SignalR upgrades/timeouts, service readiness/liveness, and resource settings. Add startup, migration, shutdown, backup/restore, and local reset instructions.
- [ ] Verify a clean local start, container recreation with retained data, bounded startup recovery, and absence of secrets in images/browser bundles.

Dependencies: T02, T04, T08, T15.  
Requirements: FR-003, FR-007, FR-008, FR-010, NFR-001, NFR-002, NFR-008.  
Done when: A documented local command starts the full topology, files are available to both API and worker, and development durability is not described as multi-host availability.

### T20 — Add GitHub Actions CI

- [x] Add PR workflows for locked restore/install, formatting, backend/frontend builds, colocated frontend specifications, Compose validation, and all three application image builds.
- [x] Keep backend, infrastructure, migration, and end-to-end tests out of CI under the current scope. Do not create or upload dedicated test-result, coverage, or repository cache artifacts.
- [ ] Document externally collected measurements with explicit size, concurrency, duration, artifact retention, and resource controls; keep real Resend calls disabled. A dedicated benchmark workflow is no longer part of the repository after the cleanup request.
- [ ] Restrict workflow permissions and secret access; avoid automatically deploying from the validation workflow.

Dependencies: T18, T19.  
Requirements: NFR-006, NFR-007, NFR-008, NFR-009.  
Done when: CI configuration is validated locally and the first GitHub run, when repository access is available, has a linked build result. If it cannot run remotely yet, record that gap rather than calling CI proven.

### T21 — Measure correctness, volume, resources, elapsed time, failures, and cost

- [ ] For a future measurement exercise, prepare seeded fixtures outside the repository and record actual file sizes/record counts, checksums, environment, commit, versions, limits, algorithm settings, and stop conditions. Do not recreate the removed benchmark tooling or data directories as part of routine implementation.
- [ ] Run the candidate size/concurrency/data-quality matrix from `design.md` where available resources permit. Separate warm-up, preserve individual repetitions, and record failed/skipped/OOM/timeout runs.
- [ ] Collect raw telemetry, API/worker/container memory with sampling provenance, stage and total time, physical I/O, scratch/output volume, retries, failure categories, and instrumentation overhead.
- [ ] Calculate cost components only from sourced rates and an explicit allocation model. Show unavailable totals and partial subtotals honestly if pricing is missing.
- [ ] Publish a reproducible benchmark report with correctness checks, measured median/range where appropriate, limitations, bottlenecks, and an evidence-based candidate operating envelope. Propose regression budgets only after review of those measurements.

Dependencies: T13, T18, T19; T20 is required before claiming CI-hosted benchmark evidence.  
Requirements: FR-005, FR-012, FR-013, NFR-001, NFR-005, NFR-009.  
Done when: At least one feasible workload has a complete evidence record for volume, memory, total time, outcome/failures, and cost status; larger unexecuted cases remain explicitly unvalidated. Missing prices are a stated limitation, not permission to invent a monetary total.

### T22 — Finalize runbooks, future deployment plan, and acceptance traceability

- [ ] Document environment setup, API/event contracts, parser/comparison semantics, retention, quotas, measurement commands, and actual configuration defaults.
- [ ] Write runbooks for stale jobs/outbox entries, worker recovery, DLQ inspection/audited replay, backup/restore, unknown email outcomes, dependency outages, and resource pressure.
- [ ] Document future hosting/storage selection, TLS/DNS, sender-domain verification, secret rotation, migrations/rollback, privacy/access controls, capacity validation, and SignalR scale-out before multiple Gateways.
- [ ] Review unverified-email limitations, supported file/configuration boundaries, validation gaps, measurement reports, and every requirement-to-task mapping. Update all three specs for accepted deviations.

Dependencies: T20, T21.  
Requirements: FR-007, FR-010, FR-011, FR-013, NFR-007, NFR-008, NFR-009, NFR-010.  
Done when: A reader can reproduce the local system and its measured evidence, see every remaining limitation, and assess a future deployment without confusing a checklist with an already deployed system.

## 3. Requirement traceability

| Requirement | Implementation and verification tasks |
| --- | --- |
| FR-001 — Identity without email confirmation | T04, T05, T15, T17, T18 |
| FR-002 — Owner isolation | T04, T05, T06, T07, T12, T14, T15, T17, T18 |
| FR-003 — Drafts and streamed upload | T06, T15, T19 |
| FR-004 — CSV format and validation | T01, T03, T06, T09, T10, T15 |
| FR-005 — Deterministic comparison | T03, T07, T10, T12, T15, T21 |
| FR-006 — Asynchronous durable submission | T03, T04, T07, T08, T11 |
| FR-007 — Retries/recovery/DLQ | T03, T04, T08, T11, T18, T19, T22 |
| FR-008 — SignalR from selection | T14, T15, T16, T18, T19 |
| FR-009 — Dashboard/history | T07, T12, T14, T16 |
| FR-010 — Storage and retention | T04, T06, T10, T12, T16, T18, T19, T22 |
| FR-011 — Explicit Resend email | T17, T18, T22 |
| FR-012 — Volume and outcomes | T09, T10, T11, T12, T13, T16, T21 |
| FR-013 — Memory/time/cost | T01, T04, T13, T16, T17, T21, T22 |
| NFR-001 — Bounded resource handling | T01, T06, T09, T10, T11, T12, T15, T19, T21 |
| NFR-002 — Security | T05, T06, T14, T17, T18, T19 |
| NFR-003 — Recovery | T04, T06, T08, T11, T12, T14, T18 |
| NFR-004 — DDD boundaries | T02, T03, T07 |
| NFR-005 — Observability | T08, T13, T21 |
| NFR-006 — Validation evidence | T02, T03, T04, T05, T08, T09, T10, T11, T17, T18, T20 |
| NFR-007 — CI | T01, T20, T22 |
| NFR-008 — Containers/future deployment | T01, T19, T20, T22 |
| NFR-009 — Reproducible measurement | T13, T20, T21, T22 |
| NFR-010 — English product | T02, T07, T15, T16, T22 |

## 4. Completion evidence template

Use this record when completing each task, either in the implementation log or its pull request:

```text
Task:
Implemented behavior:
Requirement IDs:
Files and migrations changed:
Verification commands and environment:
Observed results and artifact paths:
Measured values, or Not measured with a reason:
Known limitations / failed or skipped checks:
Specification changes:
```

Completion of documentation is not completion of the implementation backlog. Report unavailable observations as **Not measured** and missing prices as unavailable; local correctness checks do not establish production performance.
