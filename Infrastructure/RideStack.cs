using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RunDelta.API.DTO;
using RunDelta.API.Services.Ride;
using RunDelta.API.Services.Ride.Interface;
using RunDelta.API.Services.Ride.Payments;
using RunDelta.API.SignalR.Service.Rides;
using RunDelta.Infrastructure.Models;
using RunDelta.Repository.UnitOfWork.Interface;
using Stripe;

namespace ApplicationTesting.Infrastructure;

/// <summary>
/// Captures every SignalR publication so tests can assert ordering and semantics
/// without a hub. Also records a marker each time a notification is raised so
/// ordering relative to DB commits can be verified.
/// </summary>
public sealed class RecordingRideNotifier : IRideNotifier
{
    public sealed record Event(string Name, Guid? TargetUserId, RideTransactionDto Ride);

    public List<Event> Events { get; } = [];

    /// <summary>Invoked synchronously before each event is recorded.</summary>
    public Func<Event, Task>? OnPublish { get; set; }

    private async Task Record(string name, Guid? target, RideTransactionDto ride)
    {
        var e = new Event(name, target, ride);
        if (OnPublish is not null) await OnPublish(e);
        Events.Add(e);
    }

    public Task RideRequestedAsync(Guid driverUserId, RideTransactionDto ride) => Record(nameof(RideRequestedAsync), driverUserId, ride);
    public Task RideAcceptedAsync(RideTransactionDto ride) => Record(nameof(RideAcceptedAsync), null, ride);
    public Task RideDeclinedAsync(RideTransactionDto ride) => Record(nameof(RideDeclinedAsync), null, ride);
    public Task RideExpiredAsync(RideTransactionDto ride) => Record(nameof(RideExpiredAsync), null, ride);
    public Task RideStatusChangedAsync(RideTransactionDto ride) => Record(nameof(RideStatusChangedAsync), null, ride);
    public Task RideCancelledAsync(RideTransactionDto ride) => Record(nameof(RideCancelledAsync), null, ride);
    public Task RidePaymentFailedAsync(RideTransactionDto ride) => Record(nameof(RidePaymentFailedAsync), null, ride);
}

/// <summary>Controllable clock for TTL / throttle tests.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// One independent DbContext + UnitOfWork + ride services. Create two stacks
/// on the same database to simulate concurrent actors.
/// </summary>
public sealed class RideStack : IAsyncDisposable
{
    public RunDelta_DbContext Context { get; }
    public IUnitOfWork UnitOfWork { get; }
    public TestClock Clock { get; }
    public RecordingRideNotifier Notifier { get; }
    public IRideFareCalculatorFactory FareFactory { get; }
    public NearbyDriverService Nearby { get; }
    public RideLifecycleService Lifecycle { get; }
    public FakeStripeHttpClient StripeHttp { get; }
    public RidePaymentService Payments { get; }

    private RideStack(
        RunDelta_DbContext context,
        IUnitOfWork uow,
        TestClock clock,
        RecordingRideNotifier notifier,
        IRideFareCalculatorFactory fareFactory,
        NearbyDriverService nearby,
        RideLifecycleService lifecycle,
        FakeStripeHttpClient stripeHttp,
        RidePaymentService payments)
    {
        Context = context;
        UnitOfWork = uow;
        Clock = clock;
        Notifier = notifier;
        FareFactory = fareFactory;
        Nearby = nearby;
        Lifecycle = lifecycle;
        StripeHttp = stripeHttp;
        Payments = payments;
    }

    public static RideStack Create(
        LocalDbDatabase database,
        TestClock? clock = null,
        FakeStripeHttpClient? stripeHttp = null,
        IRideSettlementPolicy? settlement = null)
    {
        var context = database.CreateContext();
        var uow = BillingStack.CreateUnitOfWork(context);
        clock ??= new TestClock();
        var notifier = new RecordingRideNotifier();
        stripeHttp ??= new FakeStripeHttpClient();

        var stripeClient = new StripeClient(
            new StripeClientOptions
            {
                ApiKey = "sk_test_ride_harness",
                HttpClient = stripeHttp
            });

        var payments = new RidePaymentService(
            uow,
            stripeClient,
            settlement ?? new NoPlatformFeeSettlementPolicy(),
            Options.Create(new RidePaymentOptions()),
            clock,
            NullLogger<RidePaymentService>.Instance);

        var fareFactory = new RideFareCalculatorFactory(
        [
            new RideFareMilesCalculator(),
            new RideFareKilometerCalculator()
        ]);

        var nearby = new NearbyDriverService(
            uow, fareFactory, clock, NullLogger<NearbyDriverService>.Instance);

        var lifecycle = new RideLifecycleService(
            uow, fareFactory, notifier, payments, clock, NullLogger<RideLifecycleService>.Instance);

        return new RideStack(context, uow, clock, notifier, fareFactory, nearby, lifecycle, stripeHttp, payments);
    }

    public async ValueTask DisposeAsync()
    {
        if (UnitOfWork is IAsyncDisposable d) await d.DisposeAsync();
        await Context.DisposeAsync();
    }
}
