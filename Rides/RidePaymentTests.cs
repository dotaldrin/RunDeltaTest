using ApplicationTesting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RunDelta.API.Configuration;
using RunDelta.API.DTO;
using RunDelta.API.Services.Interface;
using RunDelta.API.Services.PlatformBilling.Interface;
using RunDelta.API.Services.Ride.Interface;
using RunDelta.API.Services.Ride.Payments;
using RunDelta.API.Services.StripeConnect;
using RunDelta.Globals.Enums;
using RunDelta.Infrastructure.Models;
using Stripe;

namespace ApplicationTesting.Rides;

/// <summary>
/// Phase 4 ride payments. Request → EnsurePayable; Accept → manual-capture
/// authorization routed to the driver's tenant Connect account; Complete →
/// capture; Decline/Cancel/Expire → release. Every Stripe side effect is
/// asserted against the recorded fake transport and committed SQL state.
/// </summary>
[TestClass]
public sealed class RidePaymentTests
{
    private const double PickupLat = 36.1627;
    private const double PickupLon = -86.7816;

    private static LocalDbDatabase _db = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _) =>
        _db = await LocalDbDatabase.CreateAsync("RidePayments");

    [ClassCleanup]
    public static async Task ClassCleanup() => await _db.DisposeAsync();

    private static CreateRideRequestDto Request(int driverId) => new()
    {
        DriverId = driverId,
        PickupLatitude = PickupLat,
        PickupLongitude = PickupLon,
        PickupAddress = "Pickup",
        DropoffLatitude = PickupLat + 0.02,
        DropoffLongitude = PickupLon,
        DropoffAddress = "Dropoff",
        DistanceMeters = 3000,
        DurationSeconds = 300
    };

    private static async Task<RideTransaction> RowAsync(int rideId)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RideTransactions.AsNoTracking().SingleAsync(r => r.Id == rideId);
    }

    private static async Task<Tenant> TenantAsync(int tenantId)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId);
    }

    private static async Task<(SeededDriver Driver, SeededRider Rider, RideTransactionDto Ride)> SeedPendingAsync(RideStack stack)
    {
        var driver = await DriverSeeder.SeedAsync(stack.Context, $"P{Guid.NewGuid():N}"[..8], PickupLat + 0.001, PickupLon);
        var rider = await RiderSeeder.SeedAsync(stack.Context);
        var ride = await stack.Lifecycle.RequestRideAsync(rider.UserId, Request(driver.DriverId));
        return (driver, rider, ride);
    }

    private static async Task<(SeededDriver Driver, SeededRider Rider, RideTransactionDto Ride)> SeedAuthorizedAsync(RideStack stack)
    {
        var seeded = await SeedPendingAsync(stack);
        var accepted = await stack.Lifecycle.AcceptAsync(seeded.Driver.UserId, seeded.Ride.Id);
        return (seeded.Driver, seeded.Rider, accepted);
    }

    private static async Task DriveToInProgressAsync(RideStack stack, Guid driverUserId, int rideId)
    {
        await stack.Lifecycle.EnRouteAsync(driverUserId, rideId);
        await stack.Lifecycle.ArrivedAsync(driverUserId, rideId);
        await stack.Lifecycle.StartAsync(driverUserId, rideId);
    }

    private static StripeConnectWebhookService CreateWebhook(RideStack stack)
    {
        var stripeClient = new StripeClient(new StripeClientOptions
        {
            ApiKey = "sk_test_ride_webhook",
            HttpClient = stack.StripeHttp
        });

        return new StripeConnectWebhookService(
            stripeClient,
            new StripeOptions
            {
                WebhookEnabled = true,
                WebhookSecret = StripeEventFactory.WebhookSecret
            },
            stack.UnitOfWork,
            Mock.Of<IStripeConnectService>(),
            Mock.Of<ITenantBillingService>(),
            stack.Payments,
            NullLogger<StripeConnectWebhookService>.Instance);
    }

    private static Dictionary<string, string> RideMetadata(RideTransaction row, int tenantId) =>
        new(StringComparer.Ordinal)
        {
            [RidePaymentMetadata.PurposeKey] = RidePaymentMetadata.PurposeValue,
            [RidePaymentMetadata.RideIdKey] = row.Id.ToString(),
            [RidePaymentMetadata.RiderUserIdKey] = row.RiderUserId.ToString(),
            [RidePaymentMetadata.DriverIdKey] = row.DriverId!.Value.ToString(),
            [RidePaymentMetadata.TenantIdKey] = tenantId.ToString()
        };

    // ------------------------------------------------------------------
    // Request boundary
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task Request_SnapshotsPaymentMethod_AndDoesNotTouchStripe()
    {
        await using var stack = RideStack.Create(_db);
        var (_, rider, ride) = await SeedPendingAsync(stack);

        var row = await RowAsync(ride.Id);
        Assert.AreEqual(rider.PaymentMethodId, row.PaymentMethodToken);
        Assert.AreEqual((int)PaymentStatus.None, row.PaymentStatus);
        Assert.IsNull(row.PaymentProviderReference);
        Assert.AreEqual(0, stack.StripeHttp.Requests.Count, "Request must not call Stripe.");
    }

    [TestMethod]
    public async Task Request_RiderWithoutCustomer_Throws_AndCreatesNoRide()
    {
        await using var stack = RideStack.Create(_db);
        var driver = await DriverSeeder.SeedAsync(stack.Context, "NoCust", PickupLat + 0.001, PickupLon);
        var rider = await RiderSeeder.SeedAsync(stack.Context, withCustomer: false);

        await Assert.ThrowsExactlyAsync<RidePaymentException>(
            () => stack.Lifecycle.RequestRideAsync(rider.UserId, Request(driver.DriverId)));

        await using var ctx = _db.CreateContext();
        Assert.IsFalse(await ctx.RideTransactions.AnyAsync(r => r.RiderUserId == rider.UserId));
    }

    [TestMethod]
    public async Task Request_RiderWithoutCard_Throws()
    {
        await using var stack = RideStack.Create(_db);
        var driver = await DriverSeeder.SeedAsync(stack.Context, "NoCard", PickupLat + 0.001, PickupLon);
        var rider = await RiderSeeder.SeedAsync(stack.Context, withCard: false);

        await Assert.ThrowsExactlyAsync<RidePaymentException>(
            () => stack.Lifecycle.RequestRideAsync(rider.UserId, Request(driver.DriverId)));
    }

    [TestMethod]
    public async Task Request_DriverTenantWithoutConnectAccount_Throws()
    {
        await using var stack = RideStack.Create(_db);

        var tenant = new Tenant
        {
            Name = "NoConnect Tenant",
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ConnectAccountId = null!
        };
        stack.Context.Tenants.Add(tenant);
        await stack.Context.SaveChangesAsync();
        stack.Context.ChangeTracker.Clear();

        var driver = await DriverSeeder.SeedAsync(stack.Context, "NoConnect", PickupLat + 0.001, PickupLon, tenantId: tenant.Id);
        var rider = await RiderSeeder.SeedAsync(stack.Context);

        await Assert.ThrowsExactlyAsync<RidePaymentException>(
            () => stack.Lifecycle.RequestRideAsync(rider.UserId, Request(driver.DriverId)));
    }

    // ------------------------------------------------------------------
    // Accept → authorize: wire contract and tenant ownership
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task Accept_AuthorizesManualCapture_ToDriverTenantConnectAccount()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, rider, ride) = await SeedAuthorizedAsync(stack);
        var tenant = await TenantAsync(driver.TenantId);
        var row = await RowAsync(ride.Id);

        var create = stack.StripeHttp.PaymentIntentCreates.Single();
        var form = create.Form;

        Assert.AreEqual(rider.StripeCustomerId, form["customer"]);
        Assert.AreEqual(rider.PaymentMethodId, form["payment_method"]);
        Assert.AreEqual("manual", form["capture_method"]);
        Assert.AreEqual("true", form["confirm"]);
        Assert.AreEqual("true", form["off_session"]);
        Assert.AreEqual("usd", form["currency"]);
        Assert.AreEqual(((long)Math.Round(row.EstimatedFare!.Value * 100m)).ToString(), form["amount"], "Amount is the server fare.");
        Assert.AreEqual(tenant.ConnectAccountId, form["transfer_data[destination]"]);
        Assert.IsFalse(form.ContainsKey("application_fee_amount"), "No platform fee under NoPlatformFeeSettlementPolicy.");
        Assert.AreEqual(RidePaymentMetadata.AuthorizeKey(ride.Id), create.IdempotencyKey);

        Assert.AreEqual(RidePaymentMetadata.PurposeValue, form[$"metadata[{RidePaymentMetadata.PurposeKey}]"]);
        Assert.AreEqual(ride.Id.ToString(), form[$"metadata[{RidePaymentMetadata.RideIdKey}]"]);
        Assert.AreEqual(rider.UserId.ToString(), form[$"metadata[{RidePaymentMetadata.RiderUserIdKey}]"]);
        Assert.AreEqual(driver.DriverId.ToString(), form[$"metadata[{RidePaymentMetadata.DriverIdKey}]"]);
        Assert.AreEqual(driver.TenantId.ToString(), form[$"metadata[{RidePaymentMetadata.TenantIdKey}]"]);

        Assert.AreEqual(RideStatus.Assigned, ride.Status);
        Assert.AreEqual(PaymentStatus.Authorized, ride.PaymentStatus);
        Assert.AreEqual((int)PaymentStatus.Authorized, row.PaymentStatus);
        Assert.IsNotNull(row.PaymentProviderReference);
        Assert.IsNotNull(row.PaymentAuthorizedAt);
        Assert.AreEqual(row.EstimatedFare, row.DriverChargedAmount);
        Assert.AreEqual(0m, row.ProviderFee);
        Assert.AreEqual(row.EstimatedFare, row.DriverPayout);
        Assert.AreEqual("requires_capture", stack.StripeHttp.CurrentStatus(row.PaymentProviderReference!));
    }

    [TestMethod]
    public async Task Accept_CrossTenant_DestinationIsDriverTenant_NotRiderTenant()
    {
        await using var stack = RideStack.Create(_db);

        var riderTenant = await TenantSeeder.SeedAsync(stack.Context, "RiderTenant");
        var unrelated = await DriverSeeder.SeedAsync(stack.Context, "Unrelated", PickupLat + 0.05, PickupLon);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);

        var driverTenant = await TenantAsync(driver.TenantId);
        var riderTenantRow = await TenantAsync(riderTenant.TenantId);
        var unrelatedTenant = await TenantAsync(unrelated.TenantId);

        var destination = stack.StripeHttp.PaymentIntentCreates.Single().Form["transfer_data[destination]"];

        Assert.AreEqual(driverTenant.ConnectAccountId, destination);
        Assert.AreNotEqual(riderTenantRow.ConnectAccountId, destination);
        Assert.AreNotEqual(unrelatedTenant.ConnectAccountId, destination);
        Assert.AreEqual(driver.TenantId.ToString(),
            stack.StripeHttp.PaymentIntentCreates.Single().Form[$"metadata[{RidePaymentMetadata.TenantIdKey}]"]);
    }

    [TestMethod]
    public async Task Accept_PlatformFee_IsPassedAsApplicationFee()
    {
        var policy = new Mock<IRideSettlementPolicy>();
        policy.Setup(p => p.SettleAsync(It.IsAny<RideTransaction>(), It.IsAny<Tenant>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RideTransaction _, Tenant _, decimal gross, CancellationToken _) =>
                new RideSettlement(gross, Math.Round(gross * 0.10m, 2), gross - Math.Round(gross * 0.10m, 2)));

        await using var stack = RideStack.Create(_db, settlement: policy.Object);
        var (_, _, ride) = await SeedAuthorizedAsync(stack);
        var row = await RowAsync(ride.Id);

        var form = stack.StripeHttp.PaymentIntentCreates.Single().Form;
        var expectedFee = (long)Math.Round(Math.Round(row.EstimatedFare!.Value * 0.10m, 2) * 100m);

        Assert.AreEqual(expectedFee.ToString(), form["application_fee_amount"]);
        Assert.AreEqual(expectedFee / 100m, row.ProviderFee);
        Assert.AreEqual(row.EstimatedFare - row.ProviderFee, row.DriverPayout);
    }

    // ------------------------------------------------------------------
    // Duplicate accept / authorize: never double charge
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task DuplicateAccept_CreatesOnlyOnePaymentIntent()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);

        await Assert.ThrowsExactlyAsync<RideStateException>(
            () => stack.Lifecycle.AcceptAsync(driver.UserId, ride.Id));

        Assert.AreEqual(1, stack.StripeHttp.PaymentIntentCreates.Count());
    }

    [TestMethod]
    public async Task DuplicateAuthorize_ResyncsExistingIntent_NoSecondCreate()
    {
        await using var stack = RideStack.Create(_db);
        var (_, _, ride) = await SeedAuthorizedAsync(stack);
        var before = await RowAsync(ride.Id);

        var again = await stack.Payments.AuthorizeAsync(ride.Id);

        Assert.IsTrue(again.Succeeded);
        Assert.AreEqual(before.PaymentProviderReference, again.PaymentIntentId);
        Assert.AreEqual(1, stack.StripeHttp.PaymentIntentCreates.Count());

        var after = await RowAsync(ride.Id);
        Assert.AreEqual(before.PaymentProviderReference, after.PaymentProviderReference);
        Assert.AreEqual((int)PaymentStatus.Authorized, after.PaymentStatus);
        Assert.AreEqual(before.PaymentAuthorizedAt, after.PaymentAuthorizedAt);
    }

    [TestMethod]
    public async Task ConcurrentAccept_TwoStacks_OnlyOneAuthorization()
    {
        var clock = new TestClock();
        await using var a = RideStack.Create(_db, clock);
        var shared = a.StripeHttp;
        await using var b = RideStack.Create(_db, clock, shared);

        var (driver, _, ride) = await SeedPendingAsync(a);

        var results = await Task.WhenAll(
            Attempt(() => a.Lifecycle.AcceptAsync(driver.UserId, ride.Id)),
            Attempt(() => b.Lifecycle.AcceptAsync(driver.UserId, ride.Id)));

        Assert.AreEqual(1, results.Count(r => r), "Exactly one accept must win.");
        Assert.AreEqual(1, shared.PaymentIntentCreates.Count(), "Loser must never authorize.");

        var row = await RowAsync(ride.Id);
        Assert.AreEqual((int)RideStatus.Assigned, row.Status);
        Assert.AreEqual((int)PaymentStatus.Authorized, row.PaymentStatus);
    }

    // ------------------------------------------------------------------
    // Failed authorization
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task Accept_CardDeclined_RevertsToPending_NotifiesFailure_NoHold()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedPendingAsync(stack);

        stack.StripeHttp.DeclineNextConfirm = true;

        await Assert.ThrowsExactlyAsync<RidePaymentException>(
            () => stack.Lifecycle.AcceptAsync(driver.UserId, ride.Id));

        var row = await RowAsync(ride.Id);
        Assert.AreEqual((int)RideStatus.Pending, row.Status, "Accept must be compensated.");
        Assert.AreEqual((int)PaymentStatus.Failed, row.PaymentStatus);
        Assert.IsNull(row.PaymentProviderReference);
        Assert.IsNull(row.PaymentAuthorizedAt);
        Assert.IsNull(row.AcceptedAt);

        Assert.IsTrue(stack.Notifier.Events.Any(e => e.Name == nameof(RecordingRideNotifier.RidePaymentFailedAsync)));
        Assert.IsFalse(stack.Notifier.Events.Any(e => e.Name == nameof(RecordingRideNotifier.RideAcceptedAsync)),
            "RideAccepted must not be published when authorization fails.");

        await using var ctx = _db.CreateContext();
        var driverRow = await ctx.Drivers.AsNoTracking().SingleAsync(d => d.Id == driver.DriverId);
        Assert.AreEqual((int)DriverAvailabilityStatus.Available, driverRow.AvailabilityStatus, "Driver must be released.");
        Assert.AreEqual((int)DriverRideStatus.None, driverRow.RideStatus);

        var history = await ctx.RideStatusHistories.AsNoTracking()
            .Where(h => h.RideTransactionId == ride.Id).OrderBy(h => h.Id).ToListAsync();
        Assert.AreEqual((int)RideStatus.Assigned, history[^1].PreviousStatus);
        Assert.AreEqual((int)RideStatus.Pending, history[^1].NewStatus);
        StringAssert.Contains(history[^1].Reason, "generic_decline", "Production surfaces Stripe's specific decline_code.");
    }

    [TestMethod]
    public async Task Accept_AfterDecline_CanRetry_AndAuthorizeSucceeds()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedPendingAsync(stack);

        stack.StripeHttp.DeclineNextConfirm = true;
        await Assert.ThrowsExactlyAsync<RidePaymentException>(
            () => stack.Lifecycle.AcceptAsync(driver.UserId, ride.Id));

        var dto = await stack.Lifecycle.AcceptAsync(driver.UserId, ride.Id);

        Assert.AreEqual(RideStatus.Assigned, dto.Status);
        Assert.AreEqual(PaymentStatus.Authorized, dto.PaymentStatus);
        Assert.AreEqual(2, stack.StripeHttp.PaymentIntentCreates.Count(), "One declined attempt, one successful.");
    }

    [TestMethod]
    public async Task Complete_WithoutAuthorization_DoesNotCapture()
    {
        await using var stack = RideStack.Create(_db);
        var (_, _, ride) = await SeedAuthorizedAsync(stack);

        // Simulate a hold that was lost between authorize and complete.
        await using (var ctx = _db.CreateContext())
        {
            var row = await ctx.RideTransactions.SingleAsync(r => r.Id == ride.Id);
            row.PaymentStatus = (int)PaymentStatus.Failed;
            await ctx.SaveChangesAsync();
        }

        // Out-of-band write: drop the stack's tracked copy, as a fresh request scope would.
        stack.Context.ChangeTracker.Clear();

        var result = await stack.Payments.CaptureAsync(ride.Id);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, stack.StripeHttp.PaymentIntentCaptures.Count());
        Assert.AreEqual((int)PaymentStatus.Failed, (await RowAsync(ride.Id)).PaymentStatus);
    }

    // ------------------------------------------------------------------
    // Complete → capture
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task Complete_CapturesAuthoritativeFare_Once()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        await DriveToInProgressAsync(stack, driver.UserId, ride.Id);

        var dto = await stack.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto());
        var row = await RowAsync(ride.Id);

        var capture = stack.StripeHttp.PaymentIntentCaptures.Single();
        var expectedCents = (long)Math.Round(row.ActualFare!.Value * 100m);

        Assert.AreEqual($"/v1/payment_intents/{row.PaymentProviderReference}/capture", capture.Path);
        Assert.AreEqual(expectedCents.ToString(), capture.Form["amount_to_capture"]);
        Assert.AreEqual(RidePaymentMetadata.CaptureKey(ride.Id), capture.IdempotencyKey);

        Assert.AreEqual(RideStatus.Completed, dto.Status);
        Assert.AreEqual(PaymentStatus.Captured, dto.PaymentStatus);
        Assert.AreEqual((int)PaymentStatus.Captured, row.PaymentStatus);
        Assert.IsNotNull(row.PaymentCapturedAt);
        Assert.AreEqual(expectedCents / 100m, row.DriverChargedAmount);
        Assert.AreEqual("succeeded", stack.StripeHttp.CurrentStatus(row.PaymentProviderReference!));
    }

    [TestMethod]
    public async Task Complete_ActualFareAboveAuthorized_CapturesOnlyAuthorizedAmount()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        await DriveToInProgressAsync(stack, driver.UserId, ride.Id);

        var authorizedCents = long.Parse(stack.StripeHttp.PaymentIntentCreates.Single().Form["amount"]);

        await stack.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto { ActualDistanceMiles = 500 });
        var row = await RowAsync(ride.Id);

        Assert.IsTrue(row.ActualFare! * 100m > authorizedCents, "Precondition: actual exceeds authorized.");
        Assert.AreEqual(authorizedCents.ToString(), stack.StripeHttp.PaymentIntentCaptures.Single().Form["amount_to_capture"]);
        Assert.AreEqual(authorizedCents / 100m, row.DriverChargedAmount);
    }

    [TestMethod]
    public async Task DuplicateCapture_SecondCallIsNoOp()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        await DriveToInProgressAsync(stack, driver.UserId, ride.Id);
        await stack.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto());
        var before = await RowAsync(ride.Id);

        var again = await stack.Payments.CaptureAsync(ride.Id);

        Assert.IsTrue(again.Succeeded);
        Assert.AreEqual(1, stack.StripeHttp.PaymentIntentCaptures.Count());

        var after = await RowAsync(ride.Id);
        Assert.AreEqual((int)PaymentStatus.Captured, after.PaymentStatus);
        Assert.AreEqual(before.PaymentCapturedAt, after.PaymentCapturedAt);
        Assert.AreEqual(before.DriverChargedAmount, after.DriverChargedAmount);
    }

    [TestMethod]
    public async Task ConcurrentComplete_TwoStacks_OnlyOneCapture()
    {
        var clock = new TestClock();
        await using var a = RideStack.Create(_db, clock);
        var shared = a.StripeHttp;
        await using var b = RideStack.Create(_db, clock, shared);

        var (driver, _, ride) = await SeedAuthorizedAsync(a);
        await DriveToInProgressAsync(a, driver.UserId, ride.Id);

        var results = await Task.WhenAll(
            Attempt(() => a.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto())),
            Attempt(() => b.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto())));

        Assert.AreEqual(1, results.Count(r => r), "Exactly one complete must win.");
        Assert.AreEqual(1, shared.PaymentIntentCaptures.Count(), "Never capture twice.");

        var row = await RowAsync(ride.Id);
        Assert.AreEqual((int)RideStatus.Completed, row.Status);
        Assert.AreEqual((int)PaymentStatus.Captured, row.PaymentStatus);
    }

    // ------------------------------------------------------------------
    // Release paths
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task Cancel_AfterAuthorization_ReleasesHold_NeverCaptures()
    {
        await using var stack = RideStack.Create(_db);
        var (_, rider, ride) = await SeedAuthorizedAsync(stack);
        var pi = (await RowAsync(ride.Id)).PaymentProviderReference!;

        var dto = await stack.Lifecycle.CancelAsync(rider.UserId, ride.Id, "Changed plans");

        var cancel = stack.StripeHttp.PaymentIntentCancels.Single();
        Assert.AreEqual($"/v1/payment_intents/{pi}/cancel", cancel.Path);
        Assert.AreEqual(RidePaymentMetadata.ReleaseKey(ride.Id), cancel.IdempotencyKey);
        Assert.AreEqual(0, stack.StripeHttp.PaymentIntentCaptures.Count());

        Assert.AreEqual(RideStatus.Cancelled, dto.Status);
        Assert.AreEqual(PaymentStatus.Released, dto.PaymentStatus);
        Assert.AreEqual((int)PaymentStatus.Released, (await RowAsync(ride.Id)).PaymentStatus);
        Assert.AreEqual("canceled", stack.StripeHttp.CurrentStatus(pi));
    }

    [TestMethod]
    public async Task Cancel_ByDriver_AfterAuthorization_ReleasesHold()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);

        await stack.Lifecycle.CancelAsync(driver.UserId, ride.Id, "Vehicle issue");

        Assert.AreEqual(1, stack.StripeHttp.PaymentIntentCancels.Count());
        Assert.AreEqual((int)PaymentStatus.Released, (await RowAsync(ride.Id)).PaymentStatus);
    }

    [TestMethod]
    public async Task Decline_BeforeAuthorization_NoStripeCalls()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedPendingAsync(stack);

        await stack.Lifecycle.DeclineAsync(driver.UserId, ride.Id, "Busy");

        Assert.AreEqual(0, stack.StripeHttp.Requests.Count);
        var row = await RowAsync(ride.Id);
        Assert.AreEqual((int)PaymentStatus.None, row.PaymentStatus);
        Assert.IsNull(row.PaymentProviderReference);
    }

    [TestMethod]
    public async Task Expire_BeforeAuthorization_NoStripeCalls()
    {
        var clock = new TestClock();
        await using var stack = RideStack.Create(_db, clock);
        var (_, _, ride) = await SeedPendingAsync(stack);

        clock.Now = clock.Now + RunDelta.API.Services.Ride.RideLifecycleService.RequestTtl + TimeSpan.FromSeconds(1);
        var dto = await stack.Lifecycle.ExpireAsync(ride.Id);

        Assert.IsNotNull(dto);
        Assert.AreEqual(0, stack.StripeHttp.Requests.Count);
        Assert.AreEqual((int)PaymentStatus.None, (await RowAsync(ride.Id)).PaymentStatus);
    }

    [TestMethod]
    public async Task Release_AfterCapture_IsNoOp_NeverCancelsCapturedIntent()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        await DriveToInProgressAsync(stack, driver.UserId, ride.Id);
        await stack.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto());

        var result = await stack.Payments.ReleaseAsync(ride.Id);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, stack.StripeHttp.PaymentIntentCancels.Count());
        Assert.AreEqual((int)PaymentStatus.Captured, (await RowAsync(ride.Id)).PaymentStatus);
    }

    [TestMethod]
    public async Task DuplicateRelease_SecondCallIsNoOp()
    {
        await using var stack = RideStack.Create(_db);
        var (_, rider, ride) = await SeedAuthorizedAsync(stack);
        await stack.Lifecycle.CancelAsync(rider.UserId, ride.Id, null);

        var again = await stack.Payments.ReleaseAsync(ride.Id);

        Assert.IsTrue(again.Succeeded);
        Assert.AreEqual(1, stack.StripeHttp.PaymentIntentCancels.Count());
    }

    // ------------------------------------------------------------------
    // Webhooks: correlation, tampering, idempotency
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task Webhook_Succeeded_MarksCaptured_ViaPaymentIntentCorrelation()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        var row = await RowAsync(ride.Id);
        var webhook = CreateWebhook(stack);

        var payload = StripeEventFactory.PaymentIntentEvent(
            "payment_intent.succeeded", row.PaymentProviderReference!, "succeeded",
            RideMetadata(row, driver.TenantId), latestChargeId: "ch_ride_1");

        var result = await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));

        Assert.IsTrue(result.Handled);
        var after = await RowAsync(ride.Id);
        Assert.AreEqual((int)PaymentStatus.Captured, after.PaymentStatus);
        Assert.IsNotNull(after.PaymentCapturedAt);
        Assert.AreEqual("ch_ride_1", after.PayoutProviderReference);
        Assert.AreEqual(0, stack.StripeHttp.PaymentIntentCaptures.Count(), "Webhook reconciles state; it must not call Stripe.");
    }

    [TestMethod]
    public async Task Webhook_Duplicate_SecondDeliveryIsNoOp()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        var row = await RowAsync(ride.Id);
        var webhook = CreateWebhook(stack);

        var payload = StripeEventFactory.PaymentIntentEvent(
            "payment_intent.succeeded", row.PaymentProviderReference!, "succeeded",
            RideMetadata(row, driver.TenantId), eventId: "evt_dup_ride");

        await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));
        var first = await RowAsync(ride.Id);

        stack.Context.ChangeTracker.Clear();
        var second = await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));
        var after = await RowAsync(ride.Id);

        Assert.IsTrue(second.Handled);
        Assert.AreEqual((int)PaymentStatus.Captured, after.PaymentStatus);
        Assert.AreEqual(first.PaymentCapturedAt, after.PaymentCapturedAt);
        Assert.AreEqual(first.RowVersion is null ? null : Convert.ToBase64String(first.RowVersion),
                        after.RowVersion is null ? null : Convert.ToBase64String(after.RowVersion),
                        "Duplicate webhook must not write.");
    }

    [TestMethod]
    public async Task Webhook_ThenBrowserCapture_ExactlyOnce()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        await DriveToInProgressAsync(stack, driver.UserId, ride.Id);
        var row = await RowAsync(ride.Id);

        // Stripe tells us first (out-of-band capture), then the driver completes.
        var webhook = CreateWebhook(stack);
        var payload = StripeEventFactory.PaymentIntentEvent(
            "payment_intent.succeeded", row.PaymentProviderReference!, "succeeded", RideMetadata(row, driver.TenantId));
        await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));
        stack.Context.ChangeTracker.Clear();

        var dto = await stack.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto());

        Assert.AreEqual(RideStatus.Completed, dto.Status);
        Assert.AreEqual(PaymentStatus.Captured, dto.PaymentStatus);
        Assert.AreEqual(0, stack.StripeHttp.PaymentIntentCaptures.Count(), "Already captured; must not capture again.");
    }

    [TestMethod]
    public async Task Webhook_ForeignTenantMetadata_IsRejected_StateUnchanged()
    {
        await using var stack = RideStack.Create(_db);
        var (_, _, ride) = await SeedAuthorizedAsync(stack);
        var foreign = await TenantSeeder.SeedAsync(stack.Context, "Foreign");
        var row = await RowAsync(ride.Id);
        var webhook = CreateWebhook(stack);

        var payload = StripeEventFactory.PaymentIntentEvent(
            "payment_intent.succeeded", row.PaymentProviderReference!, "succeeded",
            RideMetadata(row, foreign.TenantId));

        var result = await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));

        Assert.IsFalse(result.Handled);
        var after = await RowAsync(ride.Id);
        Assert.AreEqual((int)PaymentStatus.Authorized, after.PaymentStatus);
        Assert.IsNull(after.PaymentCapturedAt);
    }

    [TestMethod]
    public async Task Webhook_ForeignRideMetadata_IsRejected_StateUnchanged()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        var (_, _, otherRide) = await SeedAuthorizedAsync(stack);
        var row = await RowAsync(ride.Id);
        var webhook = CreateWebhook(stack);

        var metadata = RideMetadata(row, driver.TenantId);
        metadata[RidePaymentMetadata.RideIdKey] = otherRide.Id.ToString();

        var payload = StripeEventFactory.PaymentIntentEvent(
            "payment_intent.succeeded", row.PaymentProviderReference!, "succeeded", metadata);

        var result = await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));

        Assert.IsFalse(result.Handled);
        Assert.AreEqual((int)PaymentStatus.Authorized, (await RowAsync(ride.Id)).PaymentStatus);
        Assert.AreEqual((int)PaymentStatus.Authorized, (await RowAsync(otherRide.Id)).PaymentStatus,
            "The other ride must not be mutated either.");
    }

    [TestMethod]
    public async Task Webhook_UnknownPaymentIntent_NotHandled_NoStripeCalls()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        var row = await RowAsync(ride.Id);
        var webhook = CreateWebhook(stack);
        var requestsBefore = stack.StripeHttp.Requests.Count;

        var payload = StripeEventFactory.PaymentIntentEvent(
            "payment_intent.succeeded", "pi_does_not_exist", "succeeded", RideMetadata(row, driver.TenantId));

        var result = await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));

        Assert.IsFalse(result.Handled);
        Assert.AreEqual(requestsBefore, stack.StripeHttp.Requests.Count);
        Assert.AreEqual((int)PaymentStatus.Authorized, (await RowAsync(ride.Id)).PaymentStatus);
    }

    [TestMethod]
    public async Task Webhook_PaymentFailed_AfterCapture_DoesNotDowngrade()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        await DriveToInProgressAsync(stack, driver.UserId, ride.Id);
        await stack.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto());
        var row = await RowAsync(ride.Id);
        var webhook = CreateWebhook(stack);

        var payload = StripeEventFactory.PaymentIntentEvent(
            "payment_intent.payment_failed", row.PaymentProviderReference!, "requires_payment_method",
            RideMetadata(row, driver.TenantId), latestChargeId: null);

        var result = await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));

        Assert.IsTrue(result.Handled);
        Assert.AreEqual((int)PaymentStatus.Captured, (await RowAsync(ride.Id)).PaymentStatus);
    }

    [TestMethod]
    public async Task Webhook_Canceled_MarksReleased()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedAuthorizedAsync(stack);
        var row = await RowAsync(ride.Id);
        var webhook = CreateWebhook(stack);

        var payload = StripeEventFactory.PaymentIntentEvent(
            "payment_intent.canceled", row.PaymentProviderReference!, "canceled",
            RideMetadata(row, driver.TenantId), latestChargeId: null);

        var result = await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));

        Assert.IsTrue(result.Handled);
        Assert.AreEqual((int)PaymentStatus.Released, (await RowAsync(ride.Id)).PaymentStatus);
    }

    [TestMethod]
    public async Task Webhook_MembershipPurpose_IsNotRoutedToRidePayments()
    {
        await using var stack = RideStack.Create(_db);
        var (_, _, ride) = await SeedAuthorizedAsync(stack);
        var row = await RowAsync(ride.Id);

        var billing = new Mock<ITenantBillingService>();
        var stripeClient = new StripeClient(new StripeClientOptions { ApiKey = "sk_test", HttpClient = stack.StripeHttp });
        var webhook = new StripeConnectWebhookService(
            stripeClient,
            new StripeOptions { WebhookEnabled = true, WebhookSecret = StripeEventFactory.WebhookSecret },
            stack.UnitOfWork,
            Mock.Of<IStripeConnectService>(),
            billing.Object,
            stack.Payments,
            NullLogger<StripeConnectWebhookService>.Instance);

        // Same PaymentIntent id, but membership purpose: must go to billing, not rides.
        var payload = StripeEventFactory.PaymentIntentEvent(
            "payment_intent.succeeded", row.PaymentProviderReference!, "succeeded",
            StripeEventFactory.MembershipMetadata(tenantId: 1, productId: 1));

        await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));

        billing.Verify(b => b.FinalizeSuccessfulMembershipPaymentAsync(1, row.PaymentProviderReference!, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual((int)PaymentStatus.Authorized, (await RowAsync(ride.Id)).PaymentStatus);
    }

    private static async Task<bool> Attempt(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (RideConflictException)
        {
            return false;
        }
        catch (RideStateException)
        {
            return false;
        }
    }
}
