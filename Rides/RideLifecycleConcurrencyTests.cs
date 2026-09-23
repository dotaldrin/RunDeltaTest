using ApplicationTesting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using RunDelta.API.DTO;
using RunDelta.API.Services.Ride.Interface;
using RunDelta.Globals.Enums;

namespace ApplicationTesting.Rides;

/// <summary>
/// Concurrency and lifecycle semantics: exactly one competing transition
/// commits, RideStatusHistory agrees with the winner, and SignalR events are
/// published only after the database commit.
/// </summary>
[TestClass]
public sealed class RideLifecycleConcurrencyTests
{
    private const double PickupLat = 36.1627;
    private const double PickupLon = -86.7816;

    private static LocalDbDatabase _db = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _) =>
        _db = await LocalDbDatabase.CreateAsync("RideLifecycle");

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

    private static async Task<(SeededDriver Driver, Guid Rider, RideTransactionDto Ride)> SeedPendingAsync(RideStack stack)
    {
        var driver = await DriverSeeder.SeedAsync(stack.Context, $"D{Guid.NewGuid():N}"[..8], PickupLat + 0.001, PickupLon);
        var rider = (await RiderSeeder.SeedAsync(stack.Context)).UserId;
        var ride = await stack.Lifecycle.RequestRideAsync(rider, Request(driver.DriverId));
        return (driver, rider, ride);
    }

    private static async Task<(RideStatus Status, int CancelledBy, List<(int From, int To, string By)> History)>
        ReadStateAsync(int rideId)
    {
        await using var ctx = _db.CreateContext();
        var ride = await ctx.RideTransactions.AsNoTracking().SingleAsync(r => r.Id == rideId);
        var history = await ctx.RideStatusHistories.AsNoTracking()
            .Where(h => h.RideTransactionId == rideId)
            .OrderBy(h => h.Id)
            .Select(h => new ValueTuple<int, int, string>(h.PreviousStatus, h.NewStatus, h.ChangedBy))
            .ToListAsync();
        return ((RideStatus)ride.Status, ride.CancelledBy, history);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    [TestMethod]
    public async Task AcceptVsExpire_ExactlyOneWins()
    {
        await using var seed = RideStack.Create(_db);
        var (driver, _, ride) = await SeedPendingAsync(seed);

        // Two independent actors on separate contexts; expiry clock is past TTL.
        await using var acceptor = RideStack.Create(_db);
        await using var expirer = RideStack.Create(_db);
        expirer.Clock.Advance(TimeSpan.FromMinutes(5));

        var gate = new Barrier(2);
        var acceptTask = Task.Run(async () => { gate.SignalAndWait(); return await Capture(() => acceptor.Lifecycle.AcceptAsync(driver.UserId, ride.Id)); });
        var expireTask = Task.Run(async () => { gate.SignalAndWait(); return await Capture(() => expirer.Lifecycle.ExpireAsync(ride.Id)); });
        var results = await Task.WhenAll(acceptTask, expireTask);

        var (status, cancelledBy, history) = await ReadStateAsync(ride.Id);
        var transitions = history.Skip(1).ToList(); // skip Pending->Pending "requested"

        Assert.HasCount(1, transitions, "Exactly one transition may commit.");

        if (status == RideStatus.Assigned)
        {
            Assert.IsNull(results[0], "Accept must have succeeded.");
            Assert.AreEqual((int)RideStatus.Assigned, transitions[0].To);
            Assert.AreEqual(driver.UserId.ToString(), transitions[0].By);
            Assert.HasCount(1, acceptor.Notifier.Events.Where(e => e.Name == nameof(RecordingRideNotifier.RideAcceptedAsync)).ToList());
            Assert.IsEmpty(expirer.Notifier.Events, "Loser must not publish RideExpired.");
        }
        else
        {
            Assert.AreEqual(RideStatus.Cancelled, status);
            Assert.AreEqual((int)RideCancelledBy.System, cancelledBy);
            Assert.IsNotNull(results[0], "Accept must have failed.");
            Assert.IsTrue(results[0] is RideConflictException or RideStateException, results[0]!.GetType().Name);
            Assert.AreEqual((int)RideStatus.Cancelled, transitions[0].To);
            Assert.HasCount(1, expirer.Notifier.Events.Where(e => e.Name == nameof(RecordingRideNotifier.RideExpiredAsync)).ToList());
            Assert.IsEmpty(acceptor.Notifier.Events, "Loser must not publish RideAccepted.");
        }
    }

    [TestMethod]
    public async Task AcceptVsAccept_ExactlyOneWins()
    {
        await using var seed = RideStack.Create(_db);
        var (driver, _, ride) = await SeedPendingAsync(seed);

        await using var a = RideStack.Create(_db);
        await using var b = RideStack.Create(_db);

        var gate = new Barrier(2);
        var results = await Task.WhenAll(
            Task.Run(async () => { gate.SignalAndWait(); return await Capture(() => a.Lifecycle.AcceptAsync(driver.UserId, ride.Id)); }),
            Task.Run(async () => { gate.SignalAndWait(); return await Capture(() => b.Lifecycle.AcceptAsync(driver.UserId, ride.Id)); }));

        Assert.AreEqual(1, results.Count(r => r is null), "Exactly one accept succeeds.");
        var loser = results.Single(r => r is not null)!;
        Assert.IsTrue(loser is RideConflictException or RideStateException, loser.GetType().Name);

        var (status, _, history) = await ReadStateAsync(ride.Id);
        Assert.AreEqual(RideStatus.Assigned, status);
        Assert.HasCount(1, history.Skip(1).ToList());
        Assert.AreEqual(1, a.Notifier.Events.Count + b.Notifier.Events.Count, "Only the winner publishes.");
    }

    [TestMethod]
    public async Task CancelVsComplete_ExactlyOneWins()
    {
        await using var seed = RideStack.Create(_db);
        var (driver, rider, ride) = await SeedPendingAsync(seed);
        await seed.Lifecycle.AcceptAsync(driver.UserId, ride.Id);
        await seed.Lifecycle.StartAsync(driver.UserId, ride.Id);

        await using var canceller = RideStack.Create(_db);
        await using var completer = RideStack.Create(_db);

        var gate = new Barrier(2);
        var results = await Task.WhenAll(
            Task.Run(async () => { gate.SignalAndWait(); return await Capture(() => canceller.Lifecycle.CancelAsync(rider, ride.Id, "changed mind")); }),
            Task.Run(async () => { gate.SignalAndWait(); return await Capture(() => completer.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto())); }));

        var (status, cancelledBy, history) = await ReadStateAsync(ride.Id);
        var last = history[^1];

        // Rider cancel is not allowed once InProgress; if it raced ahead of the
        // state read it fails with RideStateException, else it loses on RowVersion.
        Assert.AreEqual(RideStatus.Completed, status);
        Assert.AreEqual((int)RideCancelledBy.None, cancelledBy);
        Assert.IsNull(results[1], "Complete must succeed.");
        Assert.IsNotNull(results[0]);
        Assert.IsTrue(results[0] is RideConflictException or RideStateException, results[0]!.GetType().Name);
        Assert.AreEqual((int)RideStatus.Completed, last.To);
        Assert.AreEqual(driver.UserId.ToString(), last.By);
        Assert.IsEmpty(canceller.Notifier.Events);
        Assert.HasCount(1, completer.Notifier.Events);
    }

    [TestMethod]
    public async Task DriverCancelVsComplete_ExactlyOneWins()
    {
        await using var seed = RideStack.Create(_db);
        var (driver, _, ride) = await SeedPendingAsync(seed);
        await seed.Lifecycle.AcceptAsync(driver.UserId, ride.Id);

        // From Assigned both driver-cancel and (via Start) complete paths compete.
        await using var canceller = RideStack.Create(_db);
        await using var starter = RideStack.Create(_db);

        var gate = new Barrier(2);
        var results = await Task.WhenAll(
            Task.Run(async () => { gate.SignalAndWait(); return await Capture(() => canceller.Lifecycle.CancelAsync(driver.UserId, ride.Id, "no show")); }),
            Task.Run(async () => { gate.SignalAndWait(); return await Capture(() => starter.Lifecycle.StartAsync(driver.UserId, ride.Id)); }));

        Assert.AreEqual(1, results.Count(r => r is null), "Exactly one transition commits.");
        var (status, cancelledBy, history) = await ReadStateAsync(ride.Id);
        var last = history[^1];

        if (status == RideStatus.Cancelled)
        {
            Assert.AreEqual((int)RideCancelledBy.Driver, cancelledBy);
            Assert.AreEqual((int)RideStatus.Cancelled, last.To);
            Assert.HasCount(1, canceller.Notifier.Events);
            Assert.IsEmpty(starter.Notifier.Events);
        }
        else
        {
            Assert.AreEqual(RideStatus.InProgress, status);
            Assert.AreEqual((int)RideStatus.InProgress, last.To);
            Assert.HasCount(1, starter.Notifier.Events);
            Assert.IsEmpty(canceller.Notifier.Events);
        }
        Assert.AreEqual((int)RideStatus.Assigned, last.From);
    }

    [TestMethod]
    public async Task Notifications_ArePublishedOnlyAfterCommit()
    {
        await using var stack = RideStack.Create(_db);
        var driver = await DriverSeeder.SeedAsync(stack.Context, "Notify", PickupLat + 0.001, PickupLon);

        // At publish time the row must already be visible from a separate connection.
        stack.Notifier.OnPublish = async e =>
        {
            await using var ctx = _db.CreateContext();
            var row = await ctx.RideTransactions.AsNoTracking().SingleOrDefaultAsync(r => r.Id == e.Ride.Id);
            Assert.IsNotNull(row, $"{e.Name} published before the ride row was committed.");
            Assert.AreEqual((int)e.Ride.Status, row.Status, $"{e.Name} published before the status change was committed.");
        };

        var ride = await stack.Lifecycle.RequestRideAsync((await RiderSeeder.SeedAsync(stack.Context)).UserId, Request(driver.DriverId));
        await stack.Lifecycle.AcceptAsync(driver.UserId, ride.Id);
        await stack.Lifecycle.EnRouteAsync(driver.UserId, ride.Id);
        await stack.Lifecycle.StartAsync(driver.UserId, ride.Id);
        await stack.Lifecycle.CompleteAsync(driver.UserId, ride.Id, new CompleteRideDto());

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(RecordingRideNotifier.RideRequestedAsync),
                nameof(RecordingRideNotifier.RideAcceptedAsync),
                nameof(RecordingRideNotifier.RideStatusChangedAsync),
                nameof(RecordingRideNotifier.RideStatusChangedAsync),
                nameof(RecordingRideNotifier.RideStatusChangedAsync)
            },
            stack.Notifier.Events.Select(e => e.Name).ToList());

        Assert.AreEqual(driver.UserId, stack.Notifier.Events[0].TargetUserId,
            "RideRequested must target the selected driver's UserId.");
    }

    [TestMethod]
    public async Task Expire_PublishesRideExpired_NotRideDeclined()
    {
        await using var stack = RideStack.Create(_db);
        var (_, _, ride) = await SeedPendingAsync(stack);
        stack.Notifier.Events.Clear();

        stack.Clock.Advance(TimeSpan.FromMinutes(5));
        var expired = await stack.Lifecycle.ExpireAsync(ride.Id);

        Assert.IsNotNull(expired);
        Assert.AreEqual(RideStatus.Cancelled, expired.Status);
        Assert.AreEqual(RideCancelledBy.System, expired.CancelledBy);

        Assert.HasCount(1, stack.Notifier.Events);
        Assert.AreEqual(nameof(RecordingRideNotifier.RideExpiredAsync), stack.Notifier.Events[0].Name);

        var (_, _, history) = await ReadStateAsync(ride.Id);
        Assert.AreEqual("system", history[^1].By.ToLowerInvariant());

        // Second expiry is a no-op with no duplicate publication.
        Assert.IsNull(await stack.Lifecycle.ExpireAsync(ride.Id));
        Assert.HasCount(1, stack.Notifier.Events);
    }

    [TestMethod]
    public async Task Decline_PublishesRideDeclined_WithDriverAttribution()
    {
        await using var stack = RideStack.Create(_db);
        var (driver, _, ride) = await SeedPendingAsync(stack);
        stack.Notifier.Events.Clear();

        var declined = await stack.Lifecycle.DeclineAsync(driver.UserId, ride.Id, "too far");

        Assert.AreEqual(RideStatus.Cancelled, declined.Status);
        Assert.AreEqual(RideCancelledBy.Driver, declined.CancelledBy);
        Assert.AreEqual(nameof(RecordingRideNotifier.RideDeclinedAsync), stack.Notifier.Events.Single().Name);
    }
}
