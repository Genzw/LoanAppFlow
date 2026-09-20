# LoanAppFlow — Distributed Loan Processing & Dynamic Rule Engine

[![.NET 10](https://img.shields.io/badge/.NET-10.0.401-512BD4?logo=dotnet&logoColor=white)](#)
[![Next.js](https://img.shields.io/badge/Next.js-16.3.5-black?logo=next.js&logoColor=white)](#)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-17-4169E1?logo=postgresql&logoColor=white)](#)
[![Playwright](https://img.shields.io/badge/Tests-153%20Passing-success?logo=playwright&logoColor=white)](#)

A production-grade distributed loan application and decisioning platform built with **.NET 10**, **Next.js 16 (React 19)**, and **PostgreSQL 17**. 

The system implements a zero-hardcoding dynamic rule engine, guaranteed at-least-once event delivery via the **Transactional Outbox Pattern**, an idempotent external partner receiver, and an end-to-end audit trail.

---

## 📺 Application Walkthrough & Demonstration Video

> 📹 **Video File:** [**Watch / Download Application Walkthrough (media/demo.mkv)**](media/demo.mkv)
> *(Also deployed live for interactive testing at [loanapp-web-production.up.railway.app](https://loanapp-web-production.up.railway.app))*
>
> **The video walks through all key flows required:**
> 1. **Approved Application**: Prime California applicant (`CA`, `$5,000`, SSN `000-00-0003`) → Instant approval receipt with Application and Customer IDs.
> 2. **Denial by State NY**: Applicant located in New York (`NY`, `$5,000`, SSN `000-00-0002`) → Triggering the state exclusion policy rule.
> 3. **Denial by Blacklisted SSN**: Applicant using SSN `000-00-0001` → Triggering the active risk blacklist rule regardless of state.
> 4. **Returning Customer Updating Existing Records**: Re-submitting with SSN `000-00-0003` with a modified requested amount or address → Customer ID is preserved and Application version is incremented from `v1` to `v2`.
> 5. **External Service Webhook Reception**: Outbox event published atomically in the same database transaction, dispatched by the background worker, and received/deduplicated by the Mock partner service.

---

## 🏛️ Architecture & Clean Code Compliance

> 📖 **Full Architectural Specification**: See [**ARCHITECTURE.md**](ARCHITECTURE.md) for detailed layer responsibilities, DDD/SOLID compliance, dynamic rule engine mechanics, transactional outbox delivery guarantees, failure rollback handling, and architectural trade-offs.

---

## 📋 Test Data & Evaluation Scenarios

Use these exact values in the application form ([`/apply`](http://127.0.0.1:3000/apply)) to reproduce each business flow:

| Scenario | State | SSN | Amount | Expected Outcome & System Behavior |
|---|---|---|---|---|
| **1. Approved Application** | `CA` (California) | `000-00-0003` | `$5,000` | **Approved**. Customer & Application created (`v1`). Outbox event enqueued and delivered. |
| **2. Denial by State NY** | `NY` (New York) | `000-00-0002` | `$5,000` | **Denied**. Reason: *Loans in New York are currently not supported*. |
| **3. Denial by Blacklist** | `CA` (Any state) | `000-00-0001` | `$5,000` | **Denied**. Reason: *Applicant SSN appears on the active underwriting exclusion list*. |
| **4. Returning Customer** | `CA` (California) | `000-00-0003` | `$8,500` | **Approved (Updated)**. Existing `CustomerId` retained, Application version incremented to `v2`, changed fields tracked. |

### Seeded Blacklisted SSNs
* `000-00-0001` *(Default seeded blacklisted identity. Additional SSNs can be added on-the-fly via the Web Admin at `/admin/rules`).*

---

## 🚀 How to Run Everything Locally

### Prerequisites
* **.NET SDK**: v10.0+
* **Node.js**: v20+ (v24 recommended)
* **PostgreSQL**: v16+ (running locally on port `5432` or via portable setup)

---

### Option A — Automated Scripts (Windows / PowerShell)

Run these copy-paste commands to initialize the portable environment and run all components:

```powershell
# 1. Setup portable .NET 10 and PostgreSQL dependencies (zero system pollution)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Setup-Portable.ps1

# 2. Seed baseline policy and initial blacklist
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Seed-Local.ps1

# 3. Launch Services in separate terminals:
# Terminal 1 — Core API & Outbox Worker (Port 5100)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Run-Local.ps1 -Component Api

# Terminal 2 — Mock External Partner Service (Port 5200)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Run-Local.ps1 -Component Mock

# Terminal 3 — Web Frontend & BFF (Port 3000)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Run-Local.ps1 -Component Web
```

---

### Option B — Standard Cross-Platform Terminal Commands

You can run each service directly using standard CLI tools:

#### 1. Start Database & Seed
Configure your local PostgreSQL connection strings in your environment or use the defaults:
```bash
# Set connection strings (replace credentials as needed)
export AppDb__ConnectionString="Host=localhost;Port=5432;Database=loanapp_api;Username=postgres;Password=postgres"
export MockDb__ConnectionString="Host=localhost;Port=5432;Database=loanapp_mock;Username=postgres;Password=postgres"
```

#### 2. Run API & Outbox Background Worker (Port 5100)
```bash
dotnet run --project src/LoanApp.Api --urls "http://127.0.0.1:5100"
```

#### 3. Run Mock External Webhook Partner (Port 5200)
```bash
dotnet run --project src/LoanApp.Mock --urls "http://127.0.0.1:5200"
```

#### 4. Run Web Application & BFF (Port 3000)
```bash
cd src/loanapp-web
npm install
npm run dev
```

---

## 🧪 How to Run Tests

The test suite covers the dynamic rule engine, returning-customer persistence, atomic rollbacks, outbox delivery, and end-to-end browser flows:

```powershell
# Run all backend unit and PostgreSQL integration tests (134 tests):
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Local.ps1

# Or run directly via dotnet CLI:
dotnet test tests/LoanApp.Tests

# Run Frontend BFF & Session authentication tests:
npm --prefix src/loanapp-web run test:session

# Run End-to-End Playwright browser tests:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Local.ps1 -Browser
```

---

## 🌐 Application Navigation & Live Endpoints

* **Executive Dashboard:** [http://127.0.0.1:3000/](http://127.0.0.1:3000/) — Diagnostics, system status, active rule catalogue.
* **Loan Origination Portal:** [http://127.0.0.1:3000/apply](http://127.0.0.1:3000/apply) — Interactive loan application with sample applicant loaders.
* **Policy Management & Rule Editor:** [http://127.0.0.1:3000/admin/rules](http://127.0.0.1:3000/admin/rules) — Create drafts, adjust conditions, manage blacklists, publish zero-downtime rules.
* **Simulation Workbench:** Integrated into `/admin/rules` — Dry-run evaluations against test payloads with condition traces.
* **Outbox Delivery Dashboard:** [http://127.0.0.1:3000/admin/applications](http://127.0.0.1:3000/admin/applications) — Real-time outbox delivery states, leases, retries, and mock partner receipts.
* **Audit Trail Viewer:** [http://127.0.0.1:3000/admin/activity](http://127.0.0.1:3000/admin/activity) — Keyset-paginated audit trail with correlation ID tracking across both API and Mock services.
* **OpenAPI 3.1 Specification:** [http://127.0.0.1:5100/openapi/v1.json](http://127.0.0.1:5100/openapi/v1.json)
