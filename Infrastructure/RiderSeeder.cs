using RunDelta.Infrastructure.Models;

namespace ApplicationTesting.Infrastructure;

public sealed record SeededRider(Guid UserId, string StripeCustomerId, string PaymentMethodId);

/// <summary>
/// Seeds a chargeable rider: business Customer row with a Stripe customer id
/// and one default saved card projection. No Identity row is required for the
/// ride-payment path.
/// </summary>
public static class RiderSeeder
{
    public static async Task<SeededRider> SeedAsync(
        RunDelta_DbContext context,
        bool withCustomer = true,
        bool withCard = true,
        Guid? userId = null)
    {
        var id = userId ?? Guid.NewGuid();
        var customerId = $"cus_test_{Guid.NewGuid():N}"[..24];
        var pmId = $"pm_test_{Guid.NewGuid():N}"[..24];

        if (withCustomer)
        {
            context.Customers.Add(new Customer
            {
                UserId = id,
                StripeCustomerId = customerId
            });

            if (withCard)
            {
                context.CustomerPaymentMethods.Add(new CustomerPaymentMethod
                {
                    UserId = id,
                    StripePaymentMethodId = pmId,
                    Brand = "visa",
                    Last4 = "4242",
                    ExpirationMonth = 12,
                    ExpirationYear = 2032,
                    IsDefault = true,
                    Priority = 1,
                    CreatedUtc = DateTime.UtcNow
                });
            }

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        return new SeededRider(id, customerId, pmId);
    }
}
