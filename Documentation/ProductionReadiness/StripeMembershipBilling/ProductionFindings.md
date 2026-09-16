# Stripe Membership Billing — Production Findings

**Source run:** ApplicationTesting baseline — 55 tests, 55 passed, 0 failed, 0 skipped
**Rule applied:** No production code was changed to make any test pass. Each finding below is asserted by a test that documents *current* behavior, so any change to that behavior is immediately visible.
**Status:** OPEN — not yet addressed. Addressing these is a separate, later phase.

---

## F-1 — Sandbox upfront charge path bypasses ledger, metadata, and deterministic idempotency

| | |
|---|---|
| **Failing / documenting test** | `SandboxChargePathTests.SandboxCharge_DoesNotCreateLedgerRow_OrMembership` |
| **Production class** | `RunDelta.API.Services.PlatformBilling.StripeTenantBillingService` |
| **Production method** | `ChargeUpfrontSandboxAsync` (exposed via `TenantBillingController` sandbox upfront-charge endpoint) |
| **Expected behavior** | Any path that moves money records a `TenantBillingTransaction`, carries membership correlation metadata (`Purpose`, `PurchaseReference`, …) so the webhook can finalize, and uses a deterministic idempotency key so a client retry cannot double-charge. |
| **Actual behavior** | Creates a `confirm=true`, `off_session=true` PaymentIntent with `RequestOptions = null` (only the Stripe SDK's random per-call key is sent), metadata lacks `Purpose`/`PurchaseReference`, and no ledger row is written. `StripeConnectWebhookService` returns `Handled=false` for the resulting `payment_intent.succeeded`. |
| **Verified cause** | Method body constructs `PaymentIntentCreateOptions` directly and calls `paymentIntentService.CreateAsync(options)` with no `RequestOptions`; it never touches `IUnitOfWork.TenantBillingTransactions`. Pricing itself is still server-authoritative (`SandboxCharge_IgnoresCallerSuppliedExistingSeatCount_PricingIsServerAuthoritative`). |
| **Impact** | Real money can move with no ledger trace and no membership activation; retry = second charge. |
| **Recommended production correction** | Either (a) remove / environment-gate the endpoint (e.g. Development only), or (b) route it through `TenantBillingService.CreateMembershipPaymentIntentAsync` so it inherits ledger, metadata, and `rundelta-membership-{tenantId}-{purchaseReference}` idempotency. Project guideline says legacy diagnostic Stripe endpoints are not changed except for compilation — owner decision required. |

---

## F-2 — Repair leaves a locally forged `Succeeded` row marked `Succeeded` when Stripe reports unpaid

| | |
|---|---|
| **Documenting test** | `MembershipRepairTests.Repair_ManuallyMarkedSucceeded_ButStripeNeverPaid_DoesNotGrantMembership` |
| **Production class** | `RunDelta.API.Services.PlatformBilling.TenantBillingService` |
| **Production method** | `RepairMembershipAsync` — `default:` branch of the Stripe-status switch |
| **Expected behavior** | If the local ledger says `Succeeded` but Stripe says `requires_payment_method` / `processing` / etc., the ledger should be corrected back to `Pending` (Stripe is source of truth) and a warning logged. |
| **Actual behavior** | No seats or membership are granted (correct), and the action reported is `LeftPending`, but the row's `Status` remains `Succeeded`. |
| **Verified cause** | The `Succeeded → Pending` correction only exists inside the `"succeeded"` case's consistency check; the `default:` branch does not inspect `transaction.Status`. `GetStatusAsync` is unaffected because it derives state from memberships, not ledger status. |
| **Impact** | Ledger misreports a payment as successful; audit/report consumers would be misled. No financial or entitlement impact. |
| **Recommended production correction** | In the `default:` branch: `if (transaction.Status == Succeeded) { Status = Pending; SucceededUtc = null; log warning with TenantId / StripePaymentIntentId; }`. Then update the test expectation to assert the row reverts to `Pending`. |

---

## F-3 — `MarkMembershipPaymentFailedAsync` opens a transaction that writes nothing

| | |
|---|---|
| **Documenting test** | `MembershipFinalizationTests.MarkPaymentFailed_LeavesLedgerPending_ForRetry` |
| **Production class** | `RunDelta.API.Services.PlatformBilling.TenantBillingService` |
| **Production method** | `MarkMembershipPaymentFailedAsync` |
| **Expected behavior** | Either record the failed attempt (`FailedUtc`, attempt count) while keeping `Status = Pending`, or do not open a database transaction. |
| **Actual behavior** | Validates the ledger row inside `IUnitOfWork.ExecuteInTransactionAsync`, then returns without mutating anything. Behavior is functionally correct (row stays `Pending` for retry). |
| **Verified cause** | Transaction body contains lookup + validation only. |
| **Impact** | No operational trace of declined attempts; unnecessary transaction overhead. Low severity. |
| **Recommended production correction** | Set `FailedUtc = utcNow` (retain `Pending`) or remove the transaction wrapper. |

---

## F-4 — Concurrent exactly-once finalization relies on implicit SQL Server locking

| | |
|---|---|
| **Proving test** | `MembershipFinalizationTests.Finalize_ConcurrentCallers_ApplySeatsExactlyOnce` — **8 concurrent workers**, each with its own `DbContext`/`UnitOfWork`, gate-released simultaneously |
| **Production class** | `RunDelta.API.Services.PlatformBilling.TenantBillingService` |
| **Production method** | `FinalizeSuccessfulMembershipPaymentAsync` |
| **Observed behavior** | PASS — seats applied exactly once, exactly one `TenantPlatformMembership` row, all 8 callers returned a finalized status. |
| **Verified mechanism** | The `UPDATE TenantBillingTransactions` inside the transaction takes a row lock that serializes competing finalizers under SQL Server READ COMMITTED; the second-and-later transactions re-read the row as `Succeeded` and short-circuit. `UX_TenantPlatformMemberships_Tenant_Product` is the backstop if two ever reach the insert. |
| **Risk** | Correctness is incidental to statement ordering and isolation level, not an explicit design choice. A future reorder (e.g. reading membership before updating the ledger) or a switch to snapshot isolation could reintroduce a race silently. |
| **Recommended production correction** | Document the invariant in code, or acquire the ledger row with an explicit `UPDLOCK, ROWLOCK` (or `SELECT … FOR UPDATE` equivalent via raw SQL) at the start of the transaction. Keep the 8-worker test as the regression guard. |

---

## Production defect vs. incorrect test expectation

During baseline bring-up, two tests initially failed. Both were **incorrect test expectations**, not production defects, and were corrected in the harness only:

| Test | Initial (wrong) expectation | Verified production behavior | Resolution |
|------|-----------------------------|------------------------------|------------|
| `Repair_IsIdempotent_OnRepeat` | Repeat repair action label `"AlreadyFinalized"` | `RepairMembershipAsync` emits `"AlreadyConsistent"` (vocabulary: `Finalized`, `MarkedCanceled`, `LeftPending`, `AlreadyConsistent`) | Test expectation corrected to the real label |
| `SandboxCharge_DoesNotCreateLedgerRow_OrMembership` | `Idempotency-Key` header absent | Stripe.net auto-generates a random idempotency key for every POST when `RequestOptions` is null, so a header is always present — the finding is that it is *not deterministic*, not that it is missing | Assertion changed to "key does not start with `rundelta-`" so F-1 remains visible |

**Decision rule used:** a failure is a *production defect* only when the observed behavior contradicts a stated business guarantee (the 15 requirements) and the cause is located in production source. A failure whose cause is a wrong constant, wrong label, or a misunderstanding of a dependency's (Stripe.net, EF Core, SQL Server) real behavior is a *test expectation error* and is fixed in `ApplicationTesting` only — never by changing production code.
