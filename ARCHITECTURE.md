# Architecture & Technical Design

LoanAppFlow is designed following **Clean Architecture**, **Domain-Driven Design (DDD)**, and **SOLID** principles. It ensures strict separation of concerns, high testability, zero-downtime policy flexibility, and guaranteed eventual consistency.

---

## 1. Project Structure & Responsibilities

```
src/
├── LoanApp.Core/          # Domain & Application Layer (Pure C#, Zero Dependencies)
│   ├── Domain/            # Entities, Value Objects, Aggregate Invariants (Submission, Customer, LoanApplication)
│   ├── Rules/             # Dynamic Rule Engine, Policy Document Schemas, Validators
│   └── Application/       # Use Cases, Ports/Interfaces (IApplicationStore, IPolicyStore), Event Contracts
│
├── LoanApp.Api/           # Infrastructure & Presentation Layer (.NET 10 Minimal APIs)
│   ├── Endpoints/         # Thin Controllers/Endpoints (ApplicationEndpoints, PolicyEndpoints, AuditEndpoints)
│   ├── Infrastructure/
│   │   ├── Persistence/   # EF Core PostgreSQL Adapters, AppDbContext, Migrations
│   │   └── Delivery/      # Transactional Outbox Background Worker, DeliveryClient, Distributed Leases
│   └── Program.cs         # Dependency Injection Composition Root, Middleware Pipeline
│
├── LoanApp.Mock/          # External Partner Mock Service (Isolated ASP.NET Core Service)
│   ├── Endpoints/         # Webhook Receiver (/events), Masked Inspection Endpoints
│   └── Infrastructure/    # Durable Inbox Deduplication, External Database Context
│
└── loanapp-web/           # Frontend & BFF (Next.js 16, React 19, TypeScript)
    ├── src/app/           # App Router Pages: Landing (/), Apply (/apply), Results, Admin Tools
    ├── src/features/      # Visual Rule Editor, Simulation Workbench, Outbox Monitor, Audit Trail
    └── src/server/        # Backend-For-Frontend (BFF) Proxy, PBKDF2/HMAC Signed Sessions
```

### Dependency Flow (Clean Architecture)
```
[ loanapp-web (UI/BFF) ] ──HTTP──▶ [ LoanApp.Api (Endpoints) ]
                                            │
                                            ▼ (Implements Interfaces)
                                   [ LoanApp.Api (Persistence/Outbox) ]
                                            │
                                            ▼ (Depends Inward)
                                   [ LoanApp.Core (Domain & Engine) ]
```
* **Domain isolation**: `LoanApp.Core` has **zero external package dependencies** (no EF Core, no ASP.NET Core, no cloud SDKs). Business logic and evaluation can run in any environment or CLI.
* **Thin controllers**: API endpoints only deserialize HTTP requests, invoke domain validators and repository ports, and return status codes.
* **Replaceable infrastructure**: Database persistence and webhook delivery implement domain interfaces (`IApplicationStore`, `IPolicyStore`, `IOutboxStore`). PostgreSQL can be replaced with any SQL provider without touching domain models.

---

## 2. Dynamic Rule Engine

### How it Works
The rule engine (`src/LoanApp.Core/Rules/RuleEngine.cs`) evaluates applicant submissions against versioned policies stored as structured `jsonb` documents in PostgreSQL:
1. **Normalization**: Both applicant input and policy definitions are normalized (trimming whitespace, formatting SSN, standardizing casing).
2. **Deterministic Evaluation**: Rules are sorted by `Priority` and evaluated sequentially.
3. **Condition Matching**: Conditions evaluate fields (e.g., `address.state`, `requestedAmount`, `ssn`) using operators (`equals`, `notEquals`, `in`, `notIn`, `greaterThan`, `lessThan`, `inBlacklist`).
4. **Denial Aggregation & Trace**: If any rule condition matches a denial criterion, denial reasons are collected and full evaluation traces are recorded for auditing and simulation.
5. **Decisiveness**: If zero denial reasons match, the application is `Approved`; otherwise, `Denied`.

### How to Add a New Rule
* **Option A — Visual Web Interface (Zero Downtime / No Code Changes)**:
  1. Open the Admin Rule Editor at `/admin/rules`.
  2. Click **Create Draft** and configure conditions (e.g., `Field: requestedAmount`, `Operator: greaterThan`, `Value: 100000`, `Denial Message: Loan amount exceeds maximum threshold`).
  3. Validate against test applicants in the **Simulator**.
  4. Click **Publish Draft**. The new version is immediately active for all subsequent requests.
* **Option B — Seed / Code**:
  Add a `RuleDefinition` to the baseline policy document in `src/LoanApp.Core/Application/Policies.cs`:
  ```csharp
  new RuleDefinition(
      Id: Guid.NewGuid(),
      Code: "HIGH_AMOUNT_RESTRICTION",
      Priority: 30,
      Match: "ALL",
      PublicMessage: "Requested loan amount exceeds state-level underwriting caps.",
      Conditions: [
          new RuleCondition("requestedAmount", "greaterThan", JsonSerializer.SerializeToElement(50000m), default)
      ]
  )
  ```

---

## 3. Background Events & External Service Delivery

```
[ User Submit ] ──▶ [ ACID Transaction in AppDbContext ]
                     ├── Save/Update Customer
                     ├── Save/Update LoanApplication
                     ├── Create OutboxMessage (Status: Pending)
                     └── Create AuditEvents
                               │ (Commit)
                               ▼
                    [ OutboxWorker Background Service ]
                               │ (Acquire Lease: FOR UPDATE SKIP LOCKED)
                               ▼
                    [ DeliveryClient (HTTP POST) ] ──▶ [ LoanApp.Mock (/events) ]
                               │                               │
                               ▼ (HTTP 200 OK)                 ▼
                    [ Mark Outbox: Delivered ]        [ Inbox Deduplication & Save ]
```

1. **Atomic Enqueue**: When a loan is approved, an `ApplicationEvent` is generated with schema versioning, customer details, and loan data. It is written to the `OutboxMessages` table in the **same database transaction** as the customer and loan records.
2. **Distributed Polling Worker**: An autonomous background hosted service (`OutboxWorker.cs`) polls PostgreSQL using distributed row leases (`FOR UPDATE SKIP LOCKED`).
3. **External Dispatch**: The worker invokes `DeliveryClient.cs`, issuing a resilient HTTP POST to the external partner mock service (`http://localhost:5200/events`).
4. **Idempotent External Inbox**: The partner service (`LoanApp.Mock`) validates the incoming `EventId` against its `Inbox` table. If the event was already processed, it acknowledges idempotently without re-executing business side effects.

---

## 4. Transaction Handling & Failure Scenarios

| Failure Scenario | Behavior & Guarantee |
|---|---|
| **Database fails during submission** | **Complete Rollback**. Because `Customer`, `LoanApplication`, `OutboxMessage`, and `AuditEvent` share a single `AppDbContext` transaction, no partial or orphaned data is ever saved. |
| **Network timeout on DB commit** | **Safe Fingerprint Verification**. `EfApplicationStore` checks whether the event ID was committed before reporting an error, avoiding blind retries or duplicate loans. |
| **External webhook is down / fails** | **Guaranteed Delivery (At-Least-Once)**. The application submission returns `Approved` immediately to the user. The `OutboxMessage` remains `Pending`/`Failed` with an incremented attempt count and exponential backoff. The worker retries automatically once the service recovers. |
| **Concurrent submissions for same SSN** | **Deterministic Concurrency Control**. PostgreSQL unique constraints (`IX_Customers_Ssn`) and optimistic concurrency tokens ensure serializable execution and prevent duplicate entity creation. |

---

## 5. Architectural Trade-offs & Deliberate Choices

* **Transactional Outbox over Kafka / RabbitMQ**:
  * *Why*: Eliminates dual-write bugs, distributed 2-phase commits, and external broker infrastructure costs. PostgreSQL provides atomic commits with outbox records for 100% data consistency.
* **Document-Driven Dynamic Rules (`jsonb`) over Compiled Rule Engine NuGet (e.g., Drools / RulesEngine)**:
  * *Why*: Enables risk officers to adjust rules, edit blacklists, and test simulator drafts via the web UI without redeploying code or restarting application servers, while remaining type-safe through domain schema validators.
* **REST / Minimal APIs over gRPC**:
  * *Why*: Provides clean, lightweight HTTP/JSON endpoints that integrate seamlessly with Next.js BFF, browser clients, and third-party webhook receivers without protocol buffer compilation overhead.
* **Returning Customer Single-Aggregate Model**:
  * *Why*: Rather than creating fragmented multiple customer profiles for the same individual, returning applicants update the existing aggregate record, incrementing the application version (`v1` → `v2`) while preserving permanent entity IDs and complete audit histories.

---

## 6. SOLID & DDD Compliance Checklist (For Reviewers)

* **S (Single Responsibility)**: `RuleEngine` only computes decisions; `EfApplicationStore` only manages persistence; `OutboxWorker` only coordinates background delivery.
* **O (Open/Closed)**: New rule operators and condition fields can be added to the schema without modifying existing business rules or database migrations.
* **L (Liskov Substitution)**: Domain contracts (`IApplicationStore`, `IPolicyStore`) can be substituted with in-memory test stubs or cloud adapters without changing application behavior.
* **I (Interface Segregation)**: Fine-grained interfaces for reading audits (`IAuditReader`), executing deliveries (`IDeliveryClient`), and managing outbox messages (`IOutboxStore`).
* **D (Dependency Inversion)**: High-level application services depend strictly on domain abstractions, with concrete EF Core and HttpClient dependencies injected at runtime.
