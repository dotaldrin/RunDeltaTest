using ApplicationTesting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using RunDelta.API.DTO.TenantBilling;

namespace ApplicationTesting.Billing;

[TestClass]
public sealed class MembershipFinalizationTests
{
    private static async Task<(SeededTenant Seed, string IntentId, int TransactionId)> CreatePendingAsync(
        BillingStack stack, string name, int seats = 2, int startingSeats = 0)
    {
        var seed = await TenantSeeder.SeedAsync(stack.Context, name, startingSeatCount: startingSeats);
        var result = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId,
            new CreateMembershipPaymentIntentRequest
            {
                ProductId = seed.ProductId,
                PurchasedSeatCount = seats,
                TenantPaymentMethodId = seed.TenantPaymentMethodId
            });
        var row = await stack.Context.TenantBillingTransactions.AsNoTracking()
            .SingleAsync(x => x.TenantBillingTransactionId == result.TenantBillingTransactionId);
        stack.Context.ChangeTracker.Clear();
        return (seed, row.StripePaymentIntentId, row.TenantBillingTransactionId);
    }

    [TestMethod]
    public async Task Finalize_ActivatesMembership_IncrementsSeats_MarksLedgerSucceeded()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "FinA", seats: 3, startingSeats: 1);

        await stack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, "ch_fin_a");

        await using var verify = VerificationDatabase.Instance.CreateContext();
        var row = await verify.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == txId);
        Assert.AreEqual(1, row.Status);
        Assert.IsNotNull(row.SucceededUtc);
        Assert.IsNull(row.FailedUtc);
        Assert.AreEqual("ch_fin_a", row.StripeChargeId);
        Assert.AreEqual(1, row.ExistingSeatCount);
        Assert.AreEqual(4, row.ResultingSeatCount);

        var quantity = await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync();
        Assert.AreEqual(4, quantity);

        var membership = await verify.TenantPlatformMemberships.SingleAsync(x => x.TenantId == seed.TenantId);
        Assert.AreEqual(3, membership.Status, "Membership must be Active (3).");
        Assert.AreEqual(seed.ProductId, membership.ProductId);
        Assert.IsNotNull(membership.EffectiveFromUtc);
        Assert.IsNull(membership.EffectiveThroughUtc);

        var status = await stack.Billing.GetStatusAsync(seed.TenantId);
        Assert.AreEqual("Active", status.MembershipStatus);
        Assert.AreEqual(4, status.PurchasedSeatCount);
    }

    [TestMethod]
    public async Task Finalize_IsIdempotent_OnRepeatCall()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, _) = await CreatePendingAsync(stack, "IdemFin", seats: 2);

        await stack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, "ch_1");
        await stack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, "ch_1");
        await stack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, null);

        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(2, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync(),
            "Seats must be applied exactly once.");
        Assert.AreEqual(1, await verify.TenantPlatformMemberships.CountAsync(x => x.TenantId == seed.TenantId));
        Assert.AreEqual(1, await verify.TenantBillingTransactions.CountAsync(x => x.TenantId == seed.TenantId && x.Status == 1));
    }

    [TestMethod]
    public async Task Finalize_DuplicateBrowserCompletion_AfterWebhook_IsSafe()
    {
        // Webhook path and browser payment-complete path both call the same finalizer
        // using separate scopes (DbContexts), as they would in production.
        await using var webhookStack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, _) = await CreatePendingAsync(webhookStack, "DupBrowser", seats: 2);
        await webhookStack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, "ch_hook");

        await using var browserStack = BillingStack.Create(VerificationDatabase.Instance);
        await browserStack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, "ch_hook");

        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(2, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync());
        Assert.AreEqual(1, await verify.TenantPlatformMemberships.CountAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task Finalize_ConcurrentCallers_ApplySeatsExactlyOnce()
    {
        await using var setup = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, _) = await CreatePendingAsync(setup, "Concurrent", seats: 5);

        const int callers = 8;
        var gate = new TaskCompletionSource();
        var stacks = Enumerable.Range(0, callers).Select(_ => BillingStack.Create(VerificationDatabase.Instance)).ToList();

        try
        {
            var tasks = stacks.Select(async s =>
            {
                await gate.Task;
                try
                {
                    await s.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, "ch_conc");
                    return (Exception?)null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }).ToList();

            gate.SetResult();
            var outcomes = await Task.WhenAll(tasks);

            await using var verify = VerificationDatabase.Instance.CreateContext();
            var quantity = await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync();
            var memberships = await verify.TenantPlatformMemberships.CountAsync(x => x.TenantId == seed.TenantId);
            var row = await verify.TenantBillingTransactions.SingleAsync(x => x.StripePaymentIntentId == intentId);

            var failures = outcomes.Where(x => x is not null).Select(x => x!.GetType().Name + ": " + x.Message).ToList();

            Assert.AreEqual(5, quantity,
                $"Seats were applied {quantity / 5.0:0.##} times under {callers} concurrent finalizers. Failures: {string.Join(" | ", failures)}");
            Assert.AreEqual(1, memberships, "Exactly one membership row may exist (UX_TenantPlatformMemberships_Tenant_Product).");
            Assert.AreEqual(1, row.Status);
            Assert.AreEqual(5, row.ResultingSeatCount);
        }
        finally
        {
            foreach (var s in stacks)
            {
                await s.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task Finalize_ForeignTenant_CannotClaimAnotherTenantsIntent()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (victim, intentId, _) = await CreatePendingAsync(stack, "IsoVictim", seats: 2);
        var attacker = await TenantSeeder.SeedAsync(stack.Context, "IsoAttacker");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            stack.Billing.FinalizeSuccessfulMembershipPaymentAsync(attacker.TenantId, intentId, "ch_x"));

        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(0, await verify.TenantProducts.Where(x => x.ProductId == victim.ProductId).Select(x => x.Quantity).SingleAsync());
        Assert.AreEqual(0, await verify.TenantProducts.Where(x => x.ProductId == attacker.ProductId).Select(x => x.Quantity).SingleAsync());
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == attacker.TenantId));
        Assert.AreEqual(0, (await verify.TenantBillingTransactions.SingleAsync(x => x.StripePaymentIntentId == intentId)).Status);
    }

    [TestMethod]
    public async Task Finalize_UnknownIntent_Throws_AndWritesNothing()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "UnknownPi");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            stack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, "pi_does_not_exist", null));

        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task Finalize_CanceledLedgerRow_IsRejected()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "CanceledRow");

        await using (var edit = VerificationDatabase.Instance.CreateContext())
        {
            var row = await edit.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == txId);
            row.Status = 3;
            await edit.SaveChangesAsync();
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            stack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, null));
    }

    [TestMethod]
    public async Task Finalize_IsAtomic_WhenSeatUpdateFails()
    {
        // Deactivate the product AFTER the intent exists: SetCurrentActiveOperatingSeatCountAsync
        // throws inside the transaction, so no ledger/membership change may be committed.
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "Atomic", seats: 2);

        await using (var edit = VerificationDatabase.Instance.CreateContext())
        {
            var product = await edit.TenantProducts.SingleAsync(x => x.ProductId == seed.ProductId);
            product.IsActive = false;
            await edit.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, "ch_atomic"));

        await using var verify = VerificationDatabase.Instance.CreateContext();
        var row = await verify.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == txId);
        Assert.AreEqual(0, row.Status, "Ledger must remain Pending when the transaction rolls back.");
        Assert.IsNull(row.SucceededUtc);
        Assert.IsNull(row.StripeChargeId);
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == seed.TenantId),
            "No membership may be committed when seat update fails.");
    }

    [TestMethod]
    public async Task Finalize_WhenSeatCountDrifted_AppliesPurchasedSeatsIncrementally()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "Drift", seats: 2, startingSeats: 1);

        await using (var edit = VerificationDatabase.Instance.CreateContext())
        {
            var product = await edit.TenantProducts.SingleAsync(x => x.ProductId == seed.ProductId);
            product.Quantity = 10; // manual DB edit between quote and payment
            await edit.SaveChangesAsync();
        }

        await stack.Billing.FinalizeSuccessfulMembershipPaymentAsync(seed.TenantId, intentId, null);

        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(12, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync());
        var row = await verify.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == txId);
        Assert.AreEqual(10, row.ExistingSeatCount);
        Assert.AreEqual(12, row.ResultingSeatCount);
    }

    [TestMethod]
    public async Task MarkPaymentFailed_LeavesLedgerPending_ForRetry()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "Failed");

        await stack.Billing.MarkMembershipPaymentFailedAsync(seed.TenantId, intentId);

        await using var verify = VerificationDatabase.Instance.CreateContext();
        var row = await verify.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == txId);
        Assert.AreEqual(0, row.Status, "Retryable card failure keeps the row Pending so the customer can retry.");
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task GetStatus_ReportsPaymentPending_WhileIntentOpen()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, _, _) = await CreatePendingAsync(stack, "StatusPending");

        var status = await stack.Billing.GetStatusAsync(seed.TenantId);
        Assert.AreEqual("PaymentPending", status.MembershipStatus);
        Assert.AreEqual(0, status.PurchasedSeatCount);
    }
}
