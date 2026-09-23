using ApplicationTesting.Infrastructure;

namespace ApplicationTesting.Billing;

/// <summary>
/// Reviews the legacy sandbox charge path. These tests document production
/// behavior that is out of scope for the Payment Element flow but still
/// reachable via POST api/TenantBilling/sandbox/upfront-charge.
/// </summary>
[TestClass]
[Ignore("StripeTenantBillingService.ChargeUpfrontSandboxAsync was removed from production; legacy sandbox charge path no longer exists.")]
public sealed class SandboxChargePathTests
{
    private static Task<Stripe.PaymentIntent> ChargeUpfrontSandboxAsync(int tenantId, int productId, int existingSeatCount, int purchasedSeatCount, string paymentMethodId)
        => throw new NotSupportedException("Legacy sandbox charge path removed.");
    [TestMethod]
    public async Task SandboxCharge_IgnoresCallerSuppliedExistingSeatCount_PricingIsServerAuthoritative()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "SandboxPrice", pricePerSeat: 50m, startingSeatCount: 3);

        await ChargeUpfrontSandboxAsync(seed.TenantId, seed.ProductId,
            existingSeatCount: 999, purchasedSeatCount: 2, seed.StripePaymentMethodId);

        var wire = stack.StripeHttp.PaymentIntentCreates.Single();
        Assert.AreEqual("9000", wire.Form["amount"], "Amount must derive from DB seat count (3) + 2 purchased, not caller input.");
        Assert.AreEqual("true", wire.Form["confirm"]);
        Assert.AreEqual("true", wire.Form["off_session"]);
    }

    [TestMethod]
    public async Task SandboxCharge_DoesNotCreateLedgerRow_OrMembership()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "SandboxLedger");

        var intent = await ChargeUpfrontSandboxAsync(seed.TenantId, seed.ProductId, 0, 1, seed.StripePaymentMethodId);

        var wire = stack.StripeHttp.PaymentIntentCreates.Single();
        Assert.IsFalse(wire.IdempotencyKey?.StartsWith("rundelta-", StringComparison.Ordinal) ?? false,
            "PRODUCTION FINDING: sandbox charge passes null RequestOptions, so only the SDK's random per-call key is sent; retries can double-charge.");
        Assert.IsFalse(wire.Form.ContainsKey("metadata[Purpose]"),
            "PRODUCTION FINDING: sandbox intents lack Purpose metadata, so the webhook ignores them and no membership is ever finalized.");
        Assert.IsFalse(wire.Form.ContainsKey("metadata[PurchaseReference]"));

        var hasLedger = stack.Context.TenantBillingTransactions.Any(x => x.StripePaymentIntentId == intent.Id);
        Assert.IsFalse(hasLedger, "Sandbox charge is not recorded in TenantBillingTransactions (money without ledger).");
    }
}
