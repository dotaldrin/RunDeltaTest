using RunDelta.Infrastructure.Models;

namespace ApplicationTesting.Infrastructure;

public sealed record SeededTenant(
    int TenantId,
    int ProductId,
    int TenantPaymentMethodId,
    string StripePaymentMethodId);

/// <summary>
/// Seeds a Tenant with a business profile, one active product (flat price per
/// seat), and one saved payment method. Returns the generated identifiers.
/// </summary>
public static class TenantSeeder
{
    public static async Task<SeededTenant> SeedAsync(
        RunDelta_DbContext context,
        string name,
        decimal pricePerSeat = 50m,
        int startingSeatCount = 0,
        string? stripeCustomerId = null)
    {
        var tenant = new Tenant
        {
            Name = name,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            StripeCustomerId = stripeCustomerId!,
            UserId = Guid.NewGuid()
        };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        context.TenantBusinessProfiles.Add(new TenantBusinessProfile
        {
            TenantId = tenant.Id,
            LegalBusinessName = $"{name} LLC",
            BusinessType = "LLC",
            BusinessEmail = $"{name.ToLowerInvariant()}@example.test",
            BusinessPhone = "5555550100",
            CreatedUtc = DateTime.UtcNow
        });

        var product = new TenantProduct
        {
            TenantId = tenant.Id,
            Name = "Platform Membership",
            Description = "Active operating seats",
            Price = pricePerSeat,
            IsActive = true,
            Quantity = startingSeatCount
        };
        context.TenantProducts.Add(product);

        var stripePaymentMethodId = $"pm_test_{Guid.NewGuid():N}"[..28];
        var paymentMethod = new TenantPaymentMethod
        {
            TenantId = tenant.Id,
            StripePaymentMethodId = stripePaymentMethodId,
            PaymentMethodType = "card",
            DisplayName = "Visa 4242",
            Last4 = "4242",
            IsDefault = true,
            Priority = 1,
            CreatedUtc = DateTime.UtcNow
        };
        context.TenantPaymentMethods.Add(paymentMethod);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();

        return new SeededTenant(
            tenant.Id,
            product.ProductId,
            paymentMethod.TenantPaymentMethodId,
            stripePaymentMethodId);
    }
}
