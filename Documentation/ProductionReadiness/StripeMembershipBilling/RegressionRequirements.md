# Stripe Membership Billing — Regression Requirements

**Applies to:** any change touching `TenantBillingController`, `TenantBillingService`, `StripeTenantBillingService`, `ActiveSeatPricingService`, `StripeConnectWebhookService`, `TenantBillingTransactionRepository`, `TenantPlatformMembershipRepository`, `TenantProductRepository`, or the `TenantBillingTransactions` / `TenantPlatformMemberships` / `TenantProducts` / `Tenants` schema in `RunDelta_DbContext`.

**Baseline:** [StripeMembershipBilling-Baseline.md](StripeMembershipBilling-Baseline.md) — 55 / 55 passed.

## Rules

1. The `ApplicationTesting` suites `PaymentIntentCreationTests`, `MembershipFinalizationTests`, `WebhookProcessingTests`, `MembershipRepairTests`, `PaymentCompleteEndpointTests`, `DatabaseConstraintTests`, and `SandboxChargePathTests` are the regression gate. They must **not be deleted, weakened, skipped, or replaced** with mocks that bypass the production `StripeClient`, `UnitOfWork`, or SQL Server schema.
2. A change is not complete until the full `ApplicationTesting` run reports **0 failed, 0 skipped**.
3. If a change intentionally alters a verified behavior, the affected test is updated **in the same change**, and the baseline document is amended with the new expected behavior and the reason.
4. A failing test is fixed in production code only if the cause is in production code. Test-expectation errors are fixed in `ApplicationTesting` only. See "Production defect vs. incorrect test expectation" in [ProductionFindings.md](ProductionFindings.md).
5. Tests documenting open findings F-1 – F-4 remain in place until the finding is resolved; at that point the test is flipped to assert the corrected behavior, not removed.

## Guarantees that must continue to be proven

| Guarantee | Minimum proof | Current tests |
|-----------|---------------|---------------|
| **Stripe wire-level request correctness** | Captured HTTP form body asserts `amount` (cents), `currency`, `customer`, `payment_method`, `confirm`, and all membership `metadata[...]` keys on `/v1/payment_intents`; `Idempotency-Key` header asserted verbatim | `PaymentIntentCreationTests.CreateMembershipPaymentIntent_*` |
| **Server-authoritative pricing** | Seat count used for pricing is read from `TenantProducts.Quantity`; no client-supplied seat count may influence the charged amount | `_UsesServerSideCurrentSeatCount_AndAuthoritativePricing`, `SandboxCharge_IgnoresCallerSuppliedExistingSeatCount_PricingIsServerAuthoritative` |
| **Transaction atomicity** | Induced failure after the ledger update but before commit leaves ledger `Pending`, no membership row, seats unchanged | `Finalize_IsAtomic_WhenSeatUpdateFails` |
| **Concurrent exactly-once finalization** | ≥ 8 concurrent workers, separate `DbContext` per worker, simultaneous release; seats applied once, one membership row | `Finalize_ConcurrentCallers_ApplySeatsExactlyOnce` (8 workers) |
| **Duplicate webhook safety** | Same signed `payment_intent.succeeded` delivered twice through separate service scopes; second delivery applies nothing | `Webhook_DuplicateDelivery_DoesNotDoubleApply` |
| **Duplicate browser-completion safety** | `payment-complete` invoked after webhook finalization, and invoked twice; no double seat application | `Finalize_DuplicateBrowserCompletion_AfterWebhook_IsSafe`, `PaymentComplete_CalledTwice_IsSafe` |
| **Repeated-repair safety** | `RepairMembershipAsync` run repeatedly returns `AlreadyConsistent`, `Repaired = 0`, seats unchanged | `Repair_IsIdempotent_OnRepeat` |
| **Webhook signature rejection** | Wrong secret, tampered body, stale timestamp, and disabled-webhook each reject before any `ITenantBillingService` call (verified with a strict mock) | `Webhook_RejectsInvalidSignature_WithoutTouchingBilling`, `_RejectsTamperedBody`, `_RejectsStaleTimestamp`, `_Disabled_ReturnsUnavailable` |
| **Tenant isolation** | Foreign payment method, foreign product, foreign PaymentIntent (service, controller, webhook-metadata), and cross-tenant repair are all rejected with no state change to the other tenant | `_RejectsForeignPaymentMethod`, `_RejectsForeignProduct`, `Finalize_ForeignTenant_CannotClaimAnotherTenantsIntent`, `Webhook_ForgedTenantMetadata_CannotFinalizeAnotherTenantsIntent`, `PaymentComplete_ForeignTenantIntent_Returns400_WithoutFinalizing`, `Repair_OnlyTouchesCallingTenant`, `Repair_IntentMetadataForDifferentTenant_Throws` |
| **SQL Server constraint enforcement** | Against a real SQL Server (LocalDB) schema generated from `RunDelta_DbContext`: `UX_TenantBillingTransactions_StripePaymentIntentId`, `UX_TenantBillingTransactions_PurchaseReference`, `UX_TenantBillingTransactions_Pending` (filtered `Status=0`), `UX_TenantPlatformMemberships_Tenant_Product`, `UX_Tenants_StripeCustomerId` (filtered `IS NOT NULL`), and the `TenantBillingTransactions → Tenants` FK each reject the violating row | `DatabaseConstraintTests.*` |

## Harness constraints that must be preserved

- Stripe traffic is intercepted at `Stripe.IHttpClient` so the production `StripeClient`/service layer executes unmodified. Do not replace this with mocks of `IStripeTenantBillingService` for the wire-level tests.
- The database is real SQL Server (LocalDB). Do not substitute the EF Core in-memory or SQLite providers — filtered unique indexes and FK enforcement would be lost.
- Webhook payloads are signed with `Stripe.EventUtility.GenerateSignatureHeader` and validated through production code. Do not bypass `ValidateSignature`.
- `RunDelta.slnx` and production source are not modified by verification work.
