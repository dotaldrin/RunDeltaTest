using ApplicationTesting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using RunDelta.API.DTO.TenantBilling;

namespace ApplicationTesting.Billing;

[TestClass]
public sealed class PaymentIntentCreationTests
{
    [TestMethod]
    public async Task CreateMembershipPaymentIntent_UsesServerSideCurrentSeatCount_AndAuthoritativePricing()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "PricingA", pricePerSeat: 50m, startingSeatCount: 3);

        var result = await stack.Billing.CreateMembershipPaymentIntentAsync(
            seed.TenantId,
            new CreateMembershipPaymentIntentRequest
            {
                ProductId = seed.ProductId,
                PurchasedSeatCount = 2,
                TenantPaymentMethodId = seed.TenantPaymentMethodId
            });

        // Existing seats come from TenantProducts.Quantity, not from the request DTO
        // (which has no ExistingSeatCount member at all).
        Assert.AreEqual(3, result.ExistingSeatCount);
        Assert.AreEqual(2, result.PurchasedSeatCount);
        Assert.AreEqual(5, result.ResultingSeatCount);
        Assert.AreEqual(100m, result.StandardAmount);
        Assert.AreEqual(10m, result.UpfrontDiscountAmount);
        Assert.AreEqual(90m, result.AmountDue);
        Assert.AreEqual("usd", result.Currency);
        Assert.IsNull(typeof(CreateMembershipPaymentIntentRequest).GetProperty("ExistingSeatCount"),
            "Request DTO must not expose ExistingSeatCount to clients.");

        var wire = stack.StripeHttp.PaymentIntentCreates.Single();
        Assert.AreEqual("9000", wire.Form["amount"], "Stripe amount must be the server quote in cents.");
        Assert.AreEqual("usd", wire.Form["currency"]);
        Assert.AreEqual("false", wire.Form["confirm"], "Payment Element path must not auto-confirm.");
        Assert.AreEqual(seed.StripePaymentMethodId, wire.Form["payment_method"]);
    }

    [TestMethod]
    public async Task CreateMembershipPaymentIntent_CreatesRequiredMetadata()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "MetaA", startingSeatCount: 1);

        await stack.Billing.CreateMembershipPaymentIntentAsync(
            seed.TenantId,
            new CreateMembershipPaymentIntentRequest
            {
                ProductId = seed.ProductId,
                PurchasedSeatCount = 4,
                TenantPaymentMethodId = seed.TenantPaymentMethodId
            });

        var form = stack.StripeHttp.PaymentIntentCreates.Single().Form;
        Assert.AreEqual(seed.TenantId.ToString(), form["metadata[RunDeltaTenantId]"]);
        Assert.AreEqual(seed.ProductId.ToString(), form["metadata[ProductId]"]);
        Assert.AreEqual("TenantPlatformMembership", form["metadata[Purpose]"]);
        Assert.AreEqual("1", form["metadata[ExistingSeatCount]"]);
        Assert.AreEqual("4", form["metadata[PurchasedSeatCount]"]);
        Assert.AreEqual("5", form["metadata[ResultingSeatCount]"]);
        Assert.AreEqual("Upfront", form["metadata[BillingMethod]"]);
        Assert.IsTrue(form.ContainsKey("metadata[PurchaseReference]"));

        var ledger = await stack.Context.TenantBillingTransactions.AsNoTracking()
            .SingleAsync(x => x.TenantId == seed.TenantId);
        Assert.AreEqual(form["metadata[PurchaseReference]"], ledger.PurchaseReference,
            "Stripe metadata PurchaseReference must correlate to the local ledger row.");
    }

    [TestMethod]
    public async Task CreateMembershipPaymentIntent_UsesDeterministicIdempotencyKey()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "IdemA");

        await stack.Billing.CreateMembershipPaymentIntentAsync(
            seed.TenantId,
            new CreateMembershipPaymentIntentRequest
            {
                ProductId = seed.ProductId,
                PurchasedSeatCount = 1,
                TenantPaymentMethodId = seed.TenantPaymentMethodId
            });

        var wire = stack.StripeHttp.PaymentIntentCreates.Single();
        var reference = wire.Form["metadata[PurchaseReference]"];
        Assert.AreEqual($"rundelta-membership-{seed.TenantId}-{reference}", wire.IdempotencyKey);

        var customer = stack.StripeHttp.CustomerCreates.Single();
        Assert.AreEqual($"rundelta-tenant-customer-{seed.TenantId}", customer.IdempotencyKey);
        Assert.AreEqual(seed.TenantId.ToString(), customer.Form["metadata[RunDeltaTenantId]"]);
    }

    [TestMethod]
    public async Task CreateMembershipPaymentIntent_PersistsPendingLedgerRow_MatchingStripeIntent()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "LedgerA", pricePerSeat: 20m, startingSeatCount: 2);

        var result = await stack.Billing.CreateMembershipPaymentIntentAsync(
            seed.TenantId,
            new CreateMembershipPaymentIntentRequest
            {
                ProductId = seed.ProductId,
                PurchasedSeatCount = 3,
                TenantPaymentMethodId = seed.TenantPaymentMethodId
            });

        var ledger = await stack.Context.TenantBillingTransactions.AsNoTracking()
            .SingleAsync(x => x.TenantBillingTransactionId == result.TenantBillingTransactionId);

        Assert.AreEqual(seed.TenantId, ledger.TenantId);
        Assert.AreEqual(seed.ProductId, ledger.ProductId);
        Assert.AreEqual(0, ledger.Status, "Status must be Pending (0).");
        Assert.AreEqual(1, ledger.TransactionType, "TransactionType must be Upfront (1).");
        Assert.AreEqual(2, ledger.ExistingSeatCount);
        Assert.AreEqual(3, ledger.PurchasedSeatCount);
        Assert.AreEqual(5, ledger.ResultingSeatCount);
        Assert.AreEqual(60m, ledger.StandardAmount);
        Assert.AreEqual(0.10m, ledger.UpfrontDiscountRate);
        Assert.AreEqual(6m, ledger.UpfrontDiscountAmount);
        Assert.AreEqual(54m, ledger.PaidAmount);
        Assert.IsNull(ledger.SucceededUtc);
        Assert.IsNull(ledger.FailedUtc);
        Assert.IsNull(ledger.StripeChargeId);
        Assert.IsTrue(ledger.StripePaymentIntentId.StartsWith("pi_", StringComparison.Ordinal));

        // Seat count must NOT change until finalization.
        var quantity = await stack.Context.TenantProducts.AsNoTracking()
            .Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync();
        Assert.AreEqual(2, quantity);
        Assert.IsFalse(await stack.Context.TenantPlatformMemberships.AnyAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task CreateMembershipPaymentIntent_ReusesOpenPendingIntent_ForSameRequest()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "ReuseA");
        var request = new CreateMembershipPaymentIntentRequest
        {
            ProductId = seed.ProductId,
            PurchasedSeatCount = 2,
            TenantPaymentMethodId = seed.TenantPaymentMethodId
        };

        var first = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId, request);
        var second = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId, request);

        Assert.AreEqual(first.TenantBillingTransactionId, second.TenantBillingTransactionId);
        Assert.AreEqual(first.ClientSecret, second.ClientSecret);
        Assert.AreEqual(1, stack.StripeHttp.PaymentIntentCreates.Count(), "No duplicate PaymentIntent may be created.");
        Assert.AreEqual(1, await stack.Context.TenantBillingTransactions.CountAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task CreateMembershipPaymentIntent_CancelsStaleIntent_WhenRequestChanges()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "StaleA");

        var first = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId,
            new CreateMembershipPaymentIntentRequest { ProductId = seed.ProductId, PurchasedSeatCount = 2, TenantPaymentMethodId = seed.TenantPaymentMethodId });
        var second = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId,
            new CreateMembershipPaymentIntentRequest { ProductId = seed.ProductId, PurchasedSeatCount = 5, TenantPaymentMethodId = seed.TenantPaymentMethodId });

        Assert.AreNotEqual(first.TenantBillingTransactionId, second.TenantBillingTransactionId);
        Assert.AreEqual(2, stack.StripeHttp.PaymentIntentCreates.Count());
        Assert.IsTrue(stack.StripeHttp.Requests.Any(r => r.Path.EndsWith("/cancel", StringComparison.Ordinal)),
            "Stale PaymentIntent must be canceled at Stripe.");

        var rows = await stack.Context.TenantBillingTransactions.AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId).OrderBy(x => x.TenantBillingTransactionId).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(3, rows[0].Status, "First row must be Canceled (3).");
        Assert.IsNotNull(rows[0].FailedUtc);
        Assert.AreEqual(0, rows[1].Status, "Second row must be Pending (0).");
        Assert.AreEqual(1, rows.Count(x => x.Status == 0), "Exactly one pending row per tenant/product.");
    }

    [TestMethod]
    public async Task CreateMembershipPaymentIntent_FinalizesAlreadySucceededPending_BeforeNewPurchase()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var seed = await TenantSeeder.SeedAsync(stack.Context, "SuccA", pricePerSeat: 10m);

        var first = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId,
            new CreateMembershipPaymentIntentRequest { ProductId = seed.ProductId, PurchasedSeatCount = 2, TenantPaymentMethodId = seed.TenantPaymentMethodId });
        var firstIntentId = (await stack.Context.TenantBillingTransactions.AsNoTracking()
            .SingleAsync(x => x.TenantBillingTransactionId == first.TenantBillingTransactionId)).StripePaymentIntentId;

        // Stripe reports the open intent as succeeded (webhook was missed).
        stack.StripeHttp.SetPaymentIntent(firstIntentId, "succeeded",
            StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var second = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId,
            new CreateMembershipPaymentIntentRequest { ProductId = seed.ProductId, PurchasedSeatCount = 1, TenantPaymentMethodId = seed.TenantPaymentMethodId });

        var firstRow = await stack.Context.TenantBillingTransactions.AsNoTracking()
            .SingleAsync(x => x.TenantBillingTransactionId == first.TenantBillingTransactionId);
        Assert.AreEqual(1, firstRow.Status, "Missed success must be finalized (Succeeded = 1).");
        Assert.AreEqual(2, second.ExistingSeatCount, "New quote must start from the finalized seat count.");
        Assert.AreEqual(3, second.ResultingSeatCount);
        Assert.IsTrue(await stack.Context.TenantPlatformMemberships.AnyAsync(x => x.TenantId == seed.TenantId && x.Status == 3));
    }

    [TestMethod]
    public async Task CreateMembershipPaymentIntent_RejectsForeignPaymentMethod()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var owner = await TenantSeeder.SeedAsync(stack.Context, "PmOwner");
        var attacker = await TenantSeeder.SeedAsync(stack.Context, "PmAttacker");

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() =>
            stack.Billing.CreateMembershipPaymentIntentAsync(attacker.TenantId,
                new CreateMembershipPaymentIntentRequest
                {
                    ProductId = attacker.ProductId,
                    PurchasedSeatCount = 1,
                    TenantPaymentMethodId = owner.TenantPaymentMethodId
                }));

        Assert.AreEqual(0, stack.StripeHttp.PaymentIntentCreates.Count());
    }

    [TestMethod]
    public async Task CreateMembershipPaymentIntent_RejectsForeignProduct()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var owner = await TenantSeeder.SeedAsync(stack.Context, "ProdOwner");
        var attacker = await TenantSeeder.SeedAsync(stack.Context, "ProdAttacker");

        await Assert.ThrowsAsync<Exception>(() =>
            stack.Billing.CreateMembershipPaymentIntentAsync(attacker.TenantId,
                new CreateMembershipPaymentIntentRequest
                {
                    ProductId = owner.ProductId,
                    PurchasedSeatCount = 1,
                    TenantPaymentMethodId = attacker.TenantPaymentMethodId
                }));

        Assert.AreEqual(0, stack.StripeHttp.PaymentIntentCreates.Count());
        Assert.AreEqual(0, await stack.Context.TenantBillingTransactions.CountAsync(x => x.TenantId == attacker.TenantId));
    }
}
