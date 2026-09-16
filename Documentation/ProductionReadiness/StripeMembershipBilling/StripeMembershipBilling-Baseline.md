# Stripe Membership Billing — Production Readiness Baseline

**Status:** BASELINE ESTABLISHED
**Harness:** `ApplicationTesting/ApplicationTesting.slnx` (external verification harness; production projects are the System Under Test)
**Result:** 55 tests / 55 passed / 0 failed (~6 s)
**Branch:** `master`

## Scope

This baseline records the verified behavior of the Stripe tenant membership billing flow at the time of verification. It is the reference point against which future regressions are measured. Production source was **not** modified during this phase; findings are recorded in [ProductionFindings.md](ProductionFindings.md) and the guarantees that must hold going forward are in [RegressionRequirements.md](RegressionRequirements.md).

## System Under Test

| Layer | Component |
|-------|-----------|
| Controller | `RunDelta.API.Controllers.TenantBillingController` (`payment-complete`, `repair`, sandbox charge) |
| Webhook | `RunDelta.API.Services.StripeConnect.StripeConnectWebhookService.ProcessAsync` |
| Orchestration | `RunDelta.API.Services.PlatformBilling.TenantBillingService` |
| Stripe adapter | `RunDelta.API.Services.PlatformBilling.StripeTenantBillingService` |
| Pricing | `RunDelta.API.Services.StandardPricingService.ActiveSeatPricingService` |
| Persistence | `RunDelta.Repository` repositories via `UnitOfWork`; schema from `RunDelta_DbContext` |

## Harness configuration

- **Database:** disposable SQL Server LocalDB created per run from the production `RunDelta_DbContext` (`EnsureCreated`), so every unique / filtered index and FK is enforced by the real engine. Azure SQL is explicitly rejected.
- **Stripe:** the production `StripeClient` is used with a recording `IHttpClient` (`FakeStripeHttpClient`). Every outbound request's method, path, form fields, metadata, and `Idempotency-Key` header are captured and asserted. No live Stripe traffic; no user-secrets required.
- **Webhooks:** payloads are signed with `Stripe.EventUtility.GenerateSignatureHeader` and validated by the production `EventUtility.ValidateSignature` path.
- **Identity:** `IAccountManagementService` is mocked only to supply the current tenant id for controller tests; everything below the controller is production code.

Run:

```powershell
dotnet build ApplicationTesting\ApplicationTesting.csproj
ApplicationTesting\bin\Debug\net10.0\ApplicationTesting.exe --report-trx
```

## Verified requirements

| # | Requirement | Suite / tests | Verdict |
|---|-------------|---------------|---------|
| 1 | PaymentIntent creation correctness | `PaymentIntentCreationTests` — amount in cents, currency, `confirm=false`, payment_method, customer | PASS |
| 2 | Server-authoritative pricing | `CreateMembershipPaymentIntent_UsesServerSideCurrentSeatCount_AndAuthoritativePricing`, `SandboxCharge_IgnoresCallerSuppliedExistingSeatCount_PricingIsServerAuthoritative` | PASS |
| 3 | Ledger creation and reconciliation | `_PersistsPendingLedgerRow_MatchingStripeIntent`, `_ReusesOpenPendingIntent_ForSameRequest`, `_CancelsStaleIntent_WhenRequestChanges`, `_FinalizesAlreadySucceededPending_BeforeNewPurchase` | PASS |
| 4 | Webhook processing | `Webhook_PaymentIntentSucceeded_FinalizesMembership`, `Webhook_PaymentFailed_LeavesPending`, `Webhook_NonMembershipPaymentIntent_IsIgnored` | PASS |
| 5 | Signature rejection | `Webhook_RejectsInvalidSignature_WithoutTouchingBilling`, `_RejectsTamperedBody`, `_RejectsStaleTimestamp`, `_Disabled_ReturnsUnavailable`, `_MembershipIntent_MissingTenantMetadata_IsRejected` | PASS |
| 6 | Successful membership finalization | `Finalize_ActivatesMembership_IncrementsSeats_MarksLedgerSucceeded`, `PaymentComplete_Succeeded_FinalizesAndReturnsActiveStatus` | PASS |
| 7 | Database transaction atomicity | `Finalize_IsAtomic_WhenSeatUpdateFails` | PASS |
| 8 | Idempotency | `_UsesDeterministicIdempotencyKey`, `Finalize_IsIdempotent_OnRepeatCall` | PASS |
| 9 | Duplicate webhook safety | `Webhook_DuplicateDelivery_DoesNotDoubleApply` | PASS |
| 10 | Duplicate browser completion safety | `Finalize_DuplicateBrowserCompletion_AfterWebhook_IsSafe`, `PaymentComplete_CalledTwice_IsSafe` | PASS |
| 11 | Concurrent finalization safety | `Finalize_ConcurrentCallers_ApplySeatsExactlyOnce` (8 parallel scopes) | PASS |
| 12 | Tenant isolation | `_RejectsForeignPaymentMethod`, `_RejectsForeignProduct`, `Finalize_ForeignTenant_CannotClaimAnotherTenantsIntent`, `Webhook_ForgedTenantMetadata_CannotFinalizeAnotherTenantsIntent`, `PaymentComplete_ForeignTenantIntent_Returns400_WithoutFinalizing`, `Repair_OnlyTouchesCallingTenant`, `Repair_IntentMetadataForDifferentTenant_Throws` | PASS |
| 13 | Repair idempotency | `Repair_IsIdempotent_OnRepeat` | PASS |
| 14 | Failure recovery | `Repair_ManuallyEditedSucceededRow_WithoutMembership_IsReplayed`, `Repair_PendingRowSucceededAtStripe_MissedWebhook_IsFinalized`, `Repair_PendingRowCanceledAtStripe_IsMarkedCanceled`, `Repair_PendingRowStillOpenAtStripe_IsLeftPending`, `MarkPaymentFailed_LeavesLedgerPending_ForRetry`, `Finalize_WhenSeatCountDrifted_AppliesPurchasedSeatsIncrementally` | PASS |
| 15 | Database constraint protection | `DatabaseConstraintTests` — unique `StripePaymentIntentId`, unique `PurchaseReference`, filtered unique Pending per tenant/product, unique membership per tenant/product, unique `StripeCustomerId` with multiple NULLs, FK orphan rejection | PASS |

## Baseline invariants observed

- `ExistingSeatCount` is always read from `TenantProducts.Quantity`; the create-intent request DTO exposes no seat-count input.
- Customer idempotency key: `rundelta-tenant-customer-{tenantId}`. PaymentIntent idempotency key: `rundelta-membership-{tenantId}-{purchaseReference}`.
- PaymentIntent metadata carries `Purpose`, `RunDeltaTenantId`, `ProductId`, `PurchaseReference`, `PurchasedSeats`, `ExistingSeatCount`, `TotalSeatCount`, `Currency`.
- Finalization runs in `IUnitOfWork.ExecuteInTransactionAsync`; seat increment, membership activation (`Status = Active`), and ledger `Succeeded` + `StripeChargeId` commit together or not at all.
- Repeat finalization / duplicate webhook / duplicate browser completion return the already-finalized state with no additional seat application.
- Retryable failures (`payment_intent.payment_failed`) leave the ledger row `Pending`.
- Repair actions vocabulary: `Finalized`, `MarkedCanceled`, `LeftPending`, `AlreadyConsistent`.

## Concurrency tests executed

| Test | Workers | Isolation | Outcome |
|------|---------|-----------|---------|
| `MembershipFinalizationTests.Finalize_ConcurrentCallers_ApplySeatsExactlyOnce` | **8** parallel finalizers | One `BillingStack` (own `RunDelta_DbContext` + `UnitOfWork`) per worker, released by a shared gate, `Task.WhenAll` | Seats applied exactly once; one `TenantPlatformMembership` row; all 8 callers received finalized status |
| `WebhookProcessingTests.Webhook_DuplicateDelivery_DoesNotDoubleApply` | 2 sequential deliveries, separate scopes | Same signed payload | Second delivery applies nothing |
| `MembershipFinalizationTests.Finalize_DuplicateBrowserCompletion_AfterWebhook_IsSafe` | webhook then browser | Separate scopes | No double application |

## SQL Server schema constraints verified

All verified on a real SQL Server LocalDB schema generated from `RunDelta_DbContext` (`DatabaseConstraintTests`):

| Constraint | Table | Definition | Test |
|-----------|-------|------------|------|
| `UX_TenantBillingTransactions_StripePaymentIntentId` | TenantBillingTransactions | unique | `Ledger_RejectsDuplicateStripePaymentIntentId` |
| `UX_TenantBillingTransactions_PurchaseReference` | TenantBillingTransactions | unique | `Ledger_RejectsDuplicatePurchaseReference` |
| `UX_TenantBillingTransactions_Pending` / `_PendingTenantProduct` | TenantBillingTransactions | unique `(TenantId, ProductId)` filtered `[Status]=0` | `Ledger_RejectsSecondPendingRow_PerTenantProduct`, `Ledger_AllowsMultipleNonPendingRows_PerTenantProduct` |
| `UX_TenantPlatformMemberships_Tenant_Product` | TenantPlatformMemberships | unique `(TenantId, ProductId)` | `Membership_RejectsDuplicateTenantProduct` |
| `UX_Tenants_StripeCustomerId` | Tenants | unique filtered `[StripeCustomerId] IS NOT NULL` | `Tenant_RejectsDuplicateStripeCustomerId`, `Tenant_AllowsMultipleNullStripeCustomerIds` |
| FK `TenantBillingTransactions.TenantId → Tenants` | TenantBillingTransactions | referential | `Ledger_RejectsOrphanTenant` |

## Webhook / security behaviors verified

| Behavior | Test | Proof |
|----------|------|-------|
| Invalid signature secret rejected | `Webhook_RejectsInvalidSignature_WithoutTouchingBilling` | `StripeWebhookValidationException`; strict `Mock<ITenantBillingService>` records zero calls |
| Body tampered after signing rejected | `Webhook_RejectsTamperedBody` | same |
| Stale timestamp (outside tolerance) rejected | `Webhook_RejectsStaleTimestamp` | same |
| `Stripe:WebhookEnabled=false` returns unavailable | `Webhook_Disabled_ReturnsUnavailable` | unavailable exception before signature check |
| Membership intent with missing/malformed `RunDeltaTenantId` rejected | `Webhook_MembershipIntent_MissingTenantMetadata_IsRejected` | no billing call |
| Non-membership PaymentIntent ignored | `Webhook_NonMembershipPaymentIntent_IsIgnored` | `Handled=false`, no billing call |
| Valid `payment_intent.succeeded` finalizes | `Webhook_PaymentIntentSucceeded_FinalizesMembership` | membership Active, ledger Succeeded |
| Valid `payment_intent.payment_failed` leaves Pending | `Webhook_PaymentFailed_LeavesPending` | ledger unchanged |
| Duplicate delivery safe | `Webhook_DuplicateDelivery_DoesNotDoubleApply` | see concurrency table |
| Controller: unauthenticated → 401 | `PaymentComplete_Unauthenticated_Returns401` | no finalization |
| Controller: intent not `succeeded` → 409 | `PaymentComplete_NotYetSucceeded_Returns409_WithoutFinalizing` | no finalization |

## Tenant-isolation behaviors verified

| Attack / mistake | Test | Result |
|------------------|------|--------|
| Create intent with another tenant's payment method | `CreateMembershipPaymentIntent_RejectsForeignPaymentMethod` | rejected, no Stripe call |
| Create intent for another tenant's product | `CreateMembershipPaymentIntent_RejectsForeignProduct` | rejected |
| Finalize another tenant's PaymentIntent via service | `Finalize_ForeignTenant_CannotClaimAnotherTenantsIntent` | rejected; owner tenant unchanged |
| Finalize another tenant's PaymentIntent via `payment-complete` | `PaymentComplete_ForeignTenantIntent_Returns400_WithoutFinalizing` | 400; no state change |
| Webhook with forged `RunDeltaTenantId` metadata | `Webhook_ForgedTenantMetadata_CannotFinalizeAnotherTenantsIntent` | rejected; no state change |
| Repair touching another tenant's rows | `Repair_OnlyTouchesCallingTenant` | only caller's rows inspected |
| Repair where Stripe metadata names a different tenant | `Repair_IntentMetadataForDifferentTenant_Throws` | throws; no state change |

## Findings

Four findings (F-1 sandbox charge path, F-2 repair leaves forged Succeeded row, F-3 no-op failure transaction, F-4 implicit locking for concurrency) are recorded with class, method, cause, and recommendation in [ProductionFindings.md](ProductionFindings.md). None were fixed in production during this phase.

## Harness bring-up notes

Two harness-only defects were corrected before baseline (no production impact):
1. `LocalDbDatabase.CreateAsync` — invalid substring on the generated database name threw `ArgumentOutOfRangeException` during `[AssemblyInitialize]`; replaced with a full GUID-based name.
2. `FakeStripeHttpClient` — per-instance ID counters produced duplicate `cus_test_000001` across `BillingStack` instances, colliding with `UX_Tenants_StripeCustomerId`; counters made static with GUID suffixes so IDs are globally unique.

Two tests then failed due to **incorrect test expectations** (not production defects) and were corrected in the harness only; see "Production defect vs. incorrect test expectation" in [ProductionFindings.md](ProductionFindings.md).
