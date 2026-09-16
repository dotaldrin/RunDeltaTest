using ApplicationTesting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using RunDelta.Infrastructure.Models;

namespace ApplicationTesting.Billing;

/// <summary>
/// Proves the SQL-first schema (as reflected by the production DbContext model)
/// blocks the duplicate rows the billing pipeline relies on never existing.
/// </summary>
[TestClass]
public sealed class DatabaseConstraintTests
{
    private static TenantBillingTransaction Ledger(int tenantId, int productId, string intentId, string reference, byte status = 0) => new()
    {
        TenantId = tenantId,
        ProductId = productId,
        StripePaymentIntentId = intentId,
        PurchaseReference = reference,
        TransactionType = 1,
        Status = status,
        PurchasedSeatCount = 1,
        ResultingSeatCount = 1,
        Currency = "usd",
        CreatedUtc = DateTime.UtcNow
    };

    [TestMethod]
    public async Task Ledger_RejectsDuplicateStripePaymentIntentId()
    {
        await using var ctx = VerificationDatabase.Instance.CreateContext();
        var seed = await TenantSeeder.SeedAsync(ctx, "CxIntent");
        var other = await TenantSeeder.SeedAsync(ctx, "CxIntent2");

        ctx.TenantBillingTransactions.Add(Ledger(seed.TenantId, seed.ProductId, "pi_dup_intent", "ref_a", status: 1));
        await ctx.SaveChangesAsync();

        ctx.TenantBillingTransactions.Add(Ledger(other.TenantId, other.ProductId, "pi_dup_intent", "ref_b", status: 1));
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Ledger_RejectsDuplicatePurchaseReference()
    {
        await using var ctx = VerificationDatabase.Instance.CreateContext();
        var seed = await TenantSeeder.SeedAsync(ctx, "CxRef");

        ctx.TenantBillingTransactions.Add(Ledger(seed.TenantId, seed.ProductId, "pi_ref_1", "ref_dup", status: 1));
        await ctx.SaveChangesAsync();

        ctx.TenantBillingTransactions.Add(Ledger(seed.TenantId, seed.ProductId, "pi_ref_2", "ref_dup", status: 1));
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Ledger_RejectsSecondPendingRow_PerTenantProduct()
    {
        await using var ctx = VerificationDatabase.Instance.CreateContext();
        var seed = await TenantSeeder.SeedAsync(ctx, "CxPending");

        ctx.TenantBillingTransactions.Add(Ledger(seed.TenantId, seed.ProductId, "pi_pend_1", "ref_p1"));
        await ctx.SaveChangesAsync();

        ctx.TenantBillingTransactions.Add(Ledger(seed.TenantId, seed.ProductId, "pi_pend_2", "ref_p2"));
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => ctx.SaveChangesAsync(),
            "Filtered unique index UX_TenantBillingTransactions_Pending must block a second Pending row.");
    }

    [TestMethod]
    public async Task Ledger_AllowsMultipleNonPendingRows_PerTenantProduct()
    {
        await using var ctx = VerificationDatabase.Instance.CreateContext();
        var seed = await TenantSeeder.SeedAsync(ctx, "CxHistory");

        ctx.TenantBillingTransactions.Add(Ledger(seed.TenantId, seed.ProductId, "pi_h1", "ref_h1", status: 1));
        ctx.TenantBillingTransactions.Add(Ledger(seed.TenantId, seed.ProductId, "pi_h2", "ref_h2", status: 1));
        ctx.TenantBillingTransactions.Add(Ledger(seed.TenantId, seed.ProductId, "pi_h3", "ref_h3", status: 3));
        ctx.TenantBillingTransactions.Add(Ledger(seed.TenantId, seed.ProductId, "pi_h4", "ref_h4"));
        await ctx.SaveChangesAsync();

        Assert.AreEqual(4, await ctx.TenantBillingTransactions.CountAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task Membership_RejectsDuplicateTenantProduct()
    {
        await using var ctx = VerificationDatabase.Instance.CreateContext();
        var seed = await TenantSeeder.SeedAsync(ctx, "CxMember");
        var now = DateTime.UtcNow;

        ctx.TenantPlatformMemberships.Add(new TenantPlatformMembership { TenantId = seed.TenantId, ProductId = seed.ProductId, Status = 3, CreatedUtc = now });
        await ctx.SaveChangesAsync();

        ctx.TenantPlatformMemberships.Add(new TenantPlatformMembership { TenantId = seed.TenantId, ProductId = seed.ProductId, Status = 3, CreatedUtc = now });
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Tenant_RejectsDuplicateStripeCustomerId()
    {
        await using var ctx = VerificationDatabase.Instance.CreateContext();
        await TenantSeeder.SeedAsync(ctx, "CxCust1", stripeCustomerId: "cus_dup_constraint");
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() =>
            TenantSeeder.SeedAsync(ctx, "CxCust2", stripeCustomerId: "cus_dup_constraint"));
    }

    [TestMethod]
    public async Task Tenant_AllowsMultipleNullStripeCustomerIds()
    {
        await using var ctx = VerificationDatabase.Instance.CreateContext();
        await TenantSeeder.SeedAsync(ctx, "CxNull1");
        await TenantSeeder.SeedAsync(ctx, "CxNull2");
    }

    [TestMethod]
    public async Task Ledger_RejectsOrphanTenant()
    {
        await using var ctx = VerificationDatabase.Instance.CreateContext();
        ctx.TenantBillingTransactions.Add(Ledger(int.MaxValue - 7, 1, "pi_orphan", "ref_orphan", status: 1));
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }
}
