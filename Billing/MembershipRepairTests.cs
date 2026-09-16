using ApplicationTesting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using RunDelta.API.DTO.TenantBilling;

namespace ApplicationTesting.Billing;

[TestClass]
public sealed class MembershipRepairTests
{
    private static async Task<(SeededTenant Seed, string IntentId, int TransactionId)> CreatePendingAsync(BillingStack stack, string name, int seats = 2)
    {
        var seed = await TenantSeeder.SeedAsync(stack.Context, name);
        var result = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId,
            new CreateMembershipPaymentIntentRequest { ProductId = seed.ProductId, PurchasedSeatCount = seats, TenantPaymentMethodId = seed.TenantPaymentMethodId });
        var row = await stack.Context.TenantBillingTransactions.AsNoTracking()
            .SingleAsync(x => x.TenantBillingTransactionId == result.TenantBillingTransactionId);
        stack.Context.ChangeTracker.Clear();
        return (seed, row.StripePaymentIntentId, row.TenantBillingTransactionId);
    }

    private static async Task ManuallyMarkSucceededAsync(int transactionId)
    {
        await using var edit = VerificationDatabase.Instance.CreateContext();
        var row = await edit.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == transactionId);
        row.Status = 1;
        row.SucceededUtc = DateTime.UtcNow;
        await edit.SaveChangesAsync();
    }

    [TestMethod]
    public async Task Repair_ManuallyEditedSucceededRow_WithoutMembership_IsReplayed()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "RepairManual", seats: 3);
        await ManuallyMarkSucceededAsync(txId);
        stack.StripeHttp.SetPaymentIntent(intentId, "succeeded", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var result = await stack.Billing.RepairMembershipAsync(seed.TenantId);

        Assert.AreEqual(1, result.ExaminedTransactionCount);
        Assert.AreEqual(1, result.RepairedTransactionCount);
        Assert.AreEqual("Finalized", result.Items.Single().Action);
        Assert.AreEqual("Active", result.Billing.MembershipStatus);
        Assert.AreEqual(3, result.Billing.PurchasedSeatCount);

        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(3, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync());
        var row = await verify.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == txId);
        Assert.AreEqual(1, row.Status);
        Assert.AreEqual("ch_test_latest", row.StripeChargeId);
    }

    [TestMethod]
    public async Task Repair_IsIdempotent_OnRepeat()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "RepairIdem", seats: 2);
        await ManuallyMarkSucceededAsync(txId);
        stack.StripeHttp.SetPaymentIntent(intentId, "succeeded", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var first = await stack.Billing.RepairMembershipAsync(seed.TenantId);
        var second = await stack.Billing.RepairMembershipAsync(seed.TenantId);
        var third = await stack.Billing.RepairMembershipAsync(seed.TenantId);

        Assert.AreEqual(1, first.RepairedTransactionCount);
        Assert.AreEqual(0, second.RepairedTransactionCount, "A fully finalized row must not be repaired again.");
        Assert.AreEqual(0, third.RepairedTransactionCount);
        Assert.AreEqual("AlreadyConsistent", second.Items.Single().Action);

        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(2, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync(),
            "Repeated repair must not re-add seats.");
        Assert.AreEqual(1, await verify.TenantPlatformMemberships.CountAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task Repair_PendingRowSucceededAtStripe_MissedWebhook_IsFinalized()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, _) = await CreatePendingAsync(stack, "RepairMissed", seats: 2);
        stack.StripeHttp.SetPaymentIntent(intentId, "succeeded", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var result = await stack.Billing.RepairMembershipAsync(seed.TenantId);

        Assert.AreEqual("Finalized", result.Items.Single().Action);
        Assert.AreEqual("Active", result.Billing.MembershipStatus);
    }

    [TestMethod]
    public async Task Repair_PendingRowCanceledAtStripe_IsMarkedCanceled()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "RepairCanceled");
        stack.StripeHttp.SetPaymentIntent(intentId, "canceled", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var result = await stack.Billing.RepairMembershipAsync(seed.TenantId);

        Assert.AreEqual("MarkedCanceled", result.Items.Single().Action);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(3, (await verify.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == txId)).Status);
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task Repair_PendingRowStillOpenAtStripe_IsLeftPending()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "RepairOpen");
        stack.StripeHttp.SetPaymentIntent(intentId, "requires_payment_method", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var result = await stack.Billing.RepairMembershipAsync(seed.TenantId);

        Assert.AreEqual("LeftPending", result.Items.Single().Action);
        Assert.AreEqual(0, result.RepairedTransactionCount);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(0, (await verify.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == txId)).Status);
    }

    [TestMethod]
    public async Task Repair_ManuallyMarkedSucceeded_ButStripeNeverPaid_DoesNotGrantMembership()
    {
        // Stripe is the source of truth: a locally forged "Succeeded" row must not grant seats.
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, txId) = await CreatePendingAsync(stack, "RepairForged", seats: 4);
        await ManuallyMarkSucceededAsync(txId);
        stack.StripeHttp.SetPaymentIntent(intentId, "requires_payment_method", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var result = await stack.Billing.RepairMembershipAsync(seed.TenantId);

        Assert.AreEqual("LeftPending", result.Items.Single().Action);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(0, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync(),
            "No seats may be granted for an unpaid intent.");
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == seed.TenantId));

        var row = await verify.TenantBillingTransactions.SingleAsync(x => x.TenantBillingTransactionId == txId);
        Assert.AreEqual(1, row.Status,
            "PRODUCTION FINDING: forged Succeeded row is left as Succeeded (not reverted to Pending) when Stripe reports unpaid.");
    }

    [TestMethod]
    public async Task Repair_OnlyTouchesCallingTenant()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (a, intentA, txA) = await CreatePendingAsync(stack, "RepairIsoA");
        var (b, intentB, txB) = await CreatePendingAsync(stack, "RepairIsoB");
        await ManuallyMarkSucceededAsync(txA);
        await ManuallyMarkSucceededAsync(txB);
        stack.StripeHttp.SetPaymentIntent(intentA, "succeeded", StripeEventFactory.MembershipMetadata(a.TenantId, a.ProductId));
        stack.StripeHttp.SetPaymentIntent(intentB, "succeeded", StripeEventFactory.MembershipMetadata(b.TenantId, b.ProductId));

        var result = await stack.Billing.RepairMembershipAsync(a.TenantId);

        Assert.AreEqual(1, result.ExaminedTransactionCount);
        Assert.AreEqual(a.TenantId, result.TenantId);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.IsTrue(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == a.TenantId));
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == b.TenantId));
        Assert.AreEqual(0, await verify.TenantProducts.Where(x => x.ProductId == b.ProductId).Select(x => x.Quantity).SingleAsync());
    }

    [TestMethod]
    public async Task Repair_IntentMetadataForDifferentTenant_Throws()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, _) = await CreatePendingAsync(stack, "RepairMeta");
        stack.StripeHttp.SetPaymentIntent(intentId, "succeeded", StripeEventFactory.MembershipMetadata(seed.TenantId + 1000, seed.ProductId));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stack.Billing.RepairMembershipAsync(seed.TenantId));
    }

    [TestMethod]
    public async Task Repair_NoTransactions_ReturnsEmptyResult()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "RepairEmpty");

        var result = await stack.Billing.RepairMembershipAsync(seed.TenantId);

        Assert.AreEqual(0, result.ExaminedTransactionCount);
        Assert.AreEqual(0, result.RepairedTransactionCount);
        Assert.AreEqual("NotConfigured", result.Billing.MembershipStatus);
    }
}
