using Microsoft.Extensions.Logging.Abstractions;
using RunDelta.API.Services.PlatformBilling;
using RunDelta.API.Services.StandardPricingService;
using RunDelta.Infrastructure.Models;
using RunDelta.Repository.Repositories;
using RunDelta.Repository.Repository;
using RunDelta.Repository.UnitOfWork;
using RunDelta.Repository.UnitOfWork.Interface;
using Stripe;

namespace ApplicationTesting.Infrastructure;

/// <summary>
/// Composes the PRODUCTION billing stack (repositories, UnitOfWork, pricing,
/// Stripe billing service, TenantBillingService) over a real DbContext and a
/// controllable Stripe transport. Nothing here re-implements production logic.
/// </summary>
public sealed class BillingStack : IAsyncDisposable
{
    public RunDelta_DbContext Context { get; }

    public IUnitOfWork UnitOfWork { get; }

    public ActiveSeatPricingService Pricing { get; }

    public StripeTenantBillingService StripeBilling { get; }

    public TenantBillingService Billing { get; }

    public FakeStripeHttpClient StripeHttp { get; }

    public IStripeClient StripeClient { get; }

    private BillingStack(
        RunDelta_DbContext context,
        IUnitOfWork unitOfWork,
        ActiveSeatPricingService pricing,
        StripeTenantBillingService stripeBilling,
        TenantBillingService billing,
        FakeStripeHttpClient stripeHttp,
        IStripeClient stripeClient)
    {
        Context = context;
        UnitOfWork = unitOfWork;
        Pricing = pricing;
        StripeBilling = stripeBilling;
        Billing = billing;
        StripeHttp = stripeHttp;
        StripeClient = stripeClient;
    }

    public static BillingStack Create(
        LocalDbDatabase database,
        FakeStripeHttpClient? stripeHttp = null)
    {
        var context = database.CreateContext();
        var http = stripeHttp ?? new FakeStripeHttpClient();
        var stripeClient = new StripeClient(
            new StripeClientOptions
            {
                ApiKey = "sk_test_applicationtesting_placeholder",
                HttpClient = http
            });

        var uow = CreateUnitOfWork(context);
        var pricing = new ActiveSeatPricingService(
            new TenantProductPricingRepository(context),
            uow.TenantProducts);
        var stripeBilling = new StripeTenantBillingService(
            uow,
            stripeClient,
            pricing);
        var billing = new TenantBillingService(
            uow,
            stripeBilling,
            pricing,
            NullLogger<TenantBillingService>.Instance);

        return new BillingStack(
            context,
            uow,
            pricing,
            stripeBilling,
            billing,
            http,
            stripeClient);
    }

    public static IUnitOfWork CreateUnitOfWork(RunDelta_DbContext context)
    {
        return new UnitOfWork(
            context,
            new TenantRepository(context, NullLogger<TenantRepository>.Instance),
            new TenantMembershipRepository(context, NullLogger<TenantMembershipRepository>.Instance),
            new TenantProductRepository(context, NullLogger<TenantProductRepository>.Instance),
            new TenantPaymentMethodRepository(context, NullLogger<TenantPaymentMethodRepository>.Instance),
            new TenantBillingTransactionRepository(context, NullLogger<TenantBillingTransactionRepository>.Instance),
            new TenantPlatformMembershipRepository(context, NullLogger<TenantPlatformMembershipRepository>.Instance),
            new DriverRepository(context, NullLogger<DriverRepository>.Instance),
            new DriverSettingRepository(context, NullLogger<DriverSettingRepository>.Instance),
            new DriverLocationRepository(context, NullLogger<DriverLocationRepository>.Instance),
            new RequestRideRepository(context, NullLogger<RequestRideRepository>.Instance),
            new RideTransactionRepository(context, NullLogger<RideTransactionRepository>.Instance),
            new RideStatusHistoryRepository(context, NullLogger<RideStatusHistoryRepository>.Instance),
            new TenantProductCategoryRepository(context, NullLogger<TenantProductCategoryRepository>.Instance),
            new TenantBusinessProfileRepository(context),
            new AddressRepository(context, NullLogger<AddressRepository>.Instance),
            new CountryRepository(context, NullLogger<CountryRepository>.Instance),
            new ContactInformationRepository(context),
            new AddressTypeRepository(context, NullLogger<AddressTypeRepository>.Instance),
            new CustomerPaymentMethodRepository(context, NullLogger<CustomerPaymentMethodRepository>.Instance),
            new CustomerRepository(context, NullLogger<CustomerRepository>.Instance),
            NullLogger<UnitOfWork>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (UnitOfWork is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }

        await Context.DisposeAsync();
    }
}
