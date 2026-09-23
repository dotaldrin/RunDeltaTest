using ApplicationTesting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using RunDelta.API.DTO;
using RunDelta.API.Services.Ride.Interface;
using RunDelta.Globals.Enums;

namespace ApplicationTesting.Rides;

/// <summary>
/// Marketplace regression guard: nearby-driver discovery must never be
/// partitioned by TenantId. Each driver's eligibility is judged against
/// its own Tenant/UserTenant/TenantMembership, never against the rider's.
/// </summary>
[TestClass]
public sealed class MarketplaceDiscoveryTests
{
    // Downtown Nashville, TN
    private const double PickupLat = 36.1627;
    private const double PickupLon = -86.7816;
    private const double DropoffLat = 36.1745; // ~1.3 mi north-ish
    private const double DropoffLon = -86.7679;

    // Discovery returns only the nearest N drivers, so every test gets its own
    // database to keep other tests' seeded drivers out of the result set.
    private LocalDbDatabase _db = null!;

    [TestInitialize]
    public async Task TestInit() =>
        _db = await LocalDbDatabase.CreateAsync("Marketplace");

    [TestCleanup]
    public async Task TestCleanup() => await _db.DisposeAsync();

    private static NearbyDriversQueryDto Query() => new()
    {
        PickupLatitude = PickupLat,
        PickupLongitude = PickupLon,
        DropoffLatitude = DropoffLat,
        DropoffLongitude = DropoffLon
    };

    private static CreateRideRequestDto Request(int driverId, double? distanceMeters = null) => new()
    {
        DriverId = driverId,
        PickupLatitude = PickupLat,
        PickupLongitude = PickupLon,
        PickupAddress = "1 Public Sq, Nashville, TN",
        DropoffLatitude = DropoffLat,
        DropoffLongitude = DropoffLon,
        DropoffAddress = "Germantown, Nashville, TN",
        DistanceMeters = distanceMeters ?? 0,
        DurationSeconds = 600
    };

    [TestMethod]
    public async Task NearbyCandidates_AreNotRestrictedByTenant()
    {
        await using var stack = RideStack.Create(_db);

        var a = await DriverSeeder.SeedAsync(stack.Context, "TenantOne", PickupLat + 0.002, PickupLon);
        var b = await DriverSeeder.SeedAsync(stack.Context, "TenantTwo", PickupLat, PickupLon + 0.003);
        var c = await DriverSeeder.SeedAsync(stack.Context, "TenantThree", PickupLat - 0.004, PickupLon);

        Assert.AreEqual(3, new[] { a.TenantId, b.TenantId, c.TenantId }.Distinct().Count(),
            "Precondition: three distinct tenants.");

        var results = await stack.Nearby.FindNearbyDriversAsync(Query());
        var ids = results.Select(r => r.DriverId).ToHashSet();

        CollectionAssert.IsSubsetOf(new[] { a.DriverId, b.DriverId, c.DriverId }, ids.ToList(),
            "All geographically eligible drivers must be returned regardless of TenantId.");
    }

    [TestMethod]
    public async Task NearbyCandidates_ValidateEachDriversOwnMembership()
    {
        await using var stack = RideStack.Create(_db);

        // Rider's tenant is unrelated to any driver tenant.
        var riderTenant = await TenantSeeder.SeedAsync(stack.Context, "RiderOrg");
        var driver = await DriverSeeder.SeedAsync(stack.Context, "OwnMembership", PickupLat + 0.001, PickupLon);
        Assert.AreNotEqual(riderTenant.TenantId, driver.TenantId);

        var results = await stack.Nearby.FindNearbyDriversAsync(Query());

        Assert.IsTrue(results.Any(r => r.DriverId == driver.DriverId),
            "Driver with active membership in its OWN tenant must be eligible even though the rider belongs elsewhere.");
    }

    [TestMethod]
    public async Task NearbyCandidates_DoNotUseRequestingTenantForMembership()
    {
        await using var stack = RideStack.Create(_db);

        // Driver has membership only in its own tenant.
        var eligible = await DriverSeeder.SeedAsync(stack.Context, "MemberOwn", PickupLat + 0.0015, PickupLon);

        // Driver whose OWN membership is inactive must be excluded, even though
        // a foreign tenant (where the rider might belong) has an active row for that user.
        var ineligible = await DriverSeeder.SeedAsync(stack.Context, "MemberForeign", PickupLat - 0.0015, PickupLon,
            activeMembership: false);
        var foreignTenant = await TenantSeeder.SeedAsync(stack.Context, "ForeignOrg");
        stack.Context.TenantMemberships.Add(new RunDelta.Infrastructure.Models.TenantMembership
        {
            TenantId = foreignTenant.TenantId,
            UserId = ineligible.UserId,
            Role = (int)TenantMembershipRole.Contractor,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow
        });
        await stack.Context.SaveChangesAsync();
        stack.Context.ChangeTracker.Clear();

        var results = await stack.Nearby.FindNearbyDriversAsync(Query());
        var ids = results.Select(r => r.DriverId).ToHashSet();

        Assert.IsTrue(ids.Contains(eligible.DriverId), "Membership check must be Driver.TenantId + Driver.UserId.");
        Assert.IsFalse(ids.Contains(ineligible.DriverId),
            "A membership row in a different tenant must not make a driver eligible; only Driver.TenantId counts.");
    }

    [TestMethod]
    public async Task DifferentTenantDriver_CanReceiveRideRequest()
    {
        await using var stack = RideStack.Create(_db);

        var riderTenant = await TenantSeeder.SeedAsync(stack.Context, "RiderCo");
        var driver = await DriverSeeder.SeedAsync(stack.Context, "OtherCo", PickupLat + 0.001, PickupLon);
        Assert.AreNotEqual(riderTenant.TenantId, driver.TenantId);

        var rider = (await RiderSeeder.SeedAsync(stack.Context)).UserId;
        var dto = await stack.Lifecycle.RequestRideAsync(rider, Request(driver.DriverId));

        Assert.AreEqual(RideStatus.Pending, dto.Status);
        Assert.AreEqual(driver.DriverId, dto.DriverId);
        Assert.AreEqual(rider, dto.RiderUserId);

        var persisted = await stack.Context.RideTransactions.AsNoTracking()
            .Include(r => r.RequestRide).SingleAsync(r => r.Id == dto.Id);
        Assert.AreEqual("pending", persisted.RequestRide.Status);
    }

    [TestMethod]
    public async Task RideTransaction_PreservesSelectedDriverOwnership()
    {
        await using var stack = RideStack.Create(_db);

        var driver = await DriverSeeder.SeedAsync(stack.Context, "Owner", PickupLat, PickupLon + 0.001);
        var dto = await stack.Lifecycle.RequestRideAsync((await RiderSeeder.SeedAsync(stack.Context)).UserId, Request(driver.DriverId));

        var ride = await stack.Context.RideTransactions.AsNoTracking()
            .Include(r => r.Driver).ThenInclude(d => d.UserTenant)
            .SingleAsync(r => r.Id == dto.Id);

        Assert.AreEqual(driver.DriverId, ride.DriverId);
        Assert.AreEqual(driver.TenantId, ride.Driver.TenantId, "Driver must retain its own Tenant.");
        Assert.AreEqual(driver.UserId, ride.Driver.UserId);
        Assert.IsNotNull(ride.Driver.UserTenant, "UserTenant relationship must be intact.");
        Assert.AreEqual(driver.TenantId, ride.Driver.UserTenant.TenantId);
    }

    [TestMethod]
    public async Task DriverSpecificPricing_IsRecomputedServerSide()
    {
        await using var stack = RideStack.Create(_db);

        var cheap = await DriverSeeder.SeedAsync(stack.Context, "Cheap", PickupLat + 0.001, PickupLon,
            baseFare: 0m, perMileRate: 2m);
        var pricey = await DriverSeeder.SeedAsync(stack.Context, "Pricey", PickupLat - 0.001, PickupLon,
            baseFare: 0m, perMileRate: 25m);

        const double tripMeters = 5 * 1609.344; // exactly 5 miles

        var r1 = await stack.Lifecycle.RequestRideAsync((await RiderSeeder.SeedAsync(stack.Context)).UserId, Request(cheap.DriverId, tripMeters));
        var r2 = await stack.Lifecycle.RequestRideAsync((await RiderSeeder.SeedAsync(stack.Context)).UserId, Request(pricey.DriverId, tripMeters));

        Assert.AreEqual(10m, r1.EstimatedFare, "5 mi * $2/mi");
        Assert.AreEqual(125m, r2.EstimatedFare, "5 mi * $25/mi");
        Assert.AreNotEqual(r1.EstimatedFare, r2.EstimatedFare);

        // Nearby estimate agrees with authoritative request-time fare.
        var options = await stack.Nearby.FindNearbyDriversAsync(new NearbyDriversQueryDto
        {
            PickupLatitude = PickupLat, PickupLongitude = PickupLon,
            DropoffLatitude = DropoffLat, DropoffLongitude = DropoffLon,
            TripDistanceMeters = tripMeters
        });
        Assert.AreEqual(10m, options.Single(o => o.DriverId == cheap.DriverId).EstimatedFare);
        Assert.AreEqual(125m, options.Single(o => o.DriverId == pricey.DriverId).EstimatedFare);
    }

    [TestMethod]
    public async Task DriverSpecificRadius_IsApplied()
    {
        await using var stack = RideStack.Create(_db);

        // Both ~2.2 km from pickup (0.02 deg lat).
        var wide = await DriverSeeder.SeedAsync(stack.Context, "WideRadius", PickupLat + 0.02, PickupLon,
            radiusMeters: 5_000);
        var narrow = await DriverSeeder.SeedAsync(stack.Context, "NarrowRadius", PickupLat - 0.02, PickupLon,
            radiusMeters: 1_000);

        var ids = (await stack.Nearby.FindNearbyDriversAsync(Query())).Select(r => r.DriverId).ToHashSet();

        Assert.IsTrue(ids.Contains(wide.DriverId));
        Assert.IsFalse(ids.Contains(narrow.DriverId),
            "Geographically near but outside that driver's own RadiusMeters must be rejected.");

        await Assert.ThrowsExactlyAsync<DriverUnavailableException>(
            () => stack.Lifecycle.RequestRideAsync(Guid.NewGuid(), Request(narrow.DriverId)));
    }

    [TestMethod]
    public async Task RadiusSlack_IsToleranceOnly_AndDoesNotAffectFare()
    {
        await using var stack = RideStack.Create(_db);

        // Driver at ~1.11 km north; radius 1_050 m. Strict check fails, 10% slack passes.
        var driver = await DriverSeeder.SeedAsync(stack.Context, "Slack", PickupLat + 0.01, PickupLon,
            baseFare: 0m, perMileRate: 2m, radiusMeters: 1_050);

        const double tripMeters = 3 * 1609.344;
        var dto = await stack.Lifecycle.RequestRideAsync((await RiderSeeder.SeedAsync(stack.Context)).UserId, Request(driver.DriverId, tripMeters));

        Assert.AreEqual(6m, dto.EstimatedFare, "Fare = distance * rate; slack (1.10) must not inflate it.");
        Assert.AreEqual(3.0, dto.EstimatedDistanceMiles, 1e-6);

        // Well beyond slack (radius 500 m) still rejects.
        var far = await DriverSeeder.SeedAsync(stack.Context, "Far", PickupLat + 0.01, PickupLon, radiusMeters: 500);
        await Assert.ThrowsExactlyAsync<DriverUnavailableException>(
            () => stack.Lifecycle.RequestRideAsync(Guid.NewGuid(), Request(far.DriverId, tripMeters)));
    }

    [TestMethod]
    public async Task CrossTenantAccept_DoesNotProduceAuthorizationFailure()
    {
        await using var stack = RideStack.Create(_db);

        var riderTenant = await TenantSeeder.SeedAsync(stack.Context, "RiderSide");
        var driver = await DriverSeeder.SeedAsync(stack.Context, "DriverSide", PickupLat + 0.001, PickupLon);
        Assert.AreNotEqual(riderTenant.TenantId, driver.TenantId);

        var dto = await stack.Lifecycle.RequestRideAsync((await RiderSeeder.SeedAsync(stack.Context)).UserId, Request(driver.DriverId));

        var accepted = await stack.Lifecycle.AcceptAsync(driver.UserId, dto.Id);
        Assert.AreEqual(RideStatus.Assigned, accepted.Status);

        // Authorization is by selected Driver/User: a stranger is rejected.
        await Assert.ThrowsExactlyAsync<RideAccessException>(
            () => stack.Lifecycle.EnRouteAsync(Guid.NewGuid(), dto.Id));
    }

    [TestMethod]
    public async Task NearbyCandidates_ExcludeIneligibleDrivers()
    {
        await using var stack = RideStack.Create(_db);

        var ok = await DriverSeeder.SeedAsync(stack.Context, "Ok", PickupLat + 0.001, PickupLon);
        var offline = await DriverSeeder.SeedAsync(stack.Context, "Offline", PickupLat + 0.001, PickupLon,
            online: DriverOnlineStatus.GoOffline);
        var busy = await DriverSeeder.SeedAsync(stack.Context, "Busy", PickupLat + 0.001, PickupLon,
            availability: DriverAvailabilityStatus.Assigned);
        var noConnect = await DriverSeeder.SeedAsync(stack.Context, "NoConnect", PickupLat + 0.001, PickupLon,
            withConnectAccount: false);
        var stale = await DriverSeeder.SeedAsync(stack.Context, "Stale", PickupLat + 0.001, PickupLon,
            locationUpdatedAt: stack.Clock.GetUtcNow().AddHours(-2));

        var ids = (await stack.Nearby.FindNearbyDriversAsync(Query())).Select(r => r.DriverId).ToHashSet();

        Assert.IsTrue(ids.Contains(ok.DriverId));
        Assert.IsFalse(ids.Contains(offline.DriverId));
        Assert.IsFalse(ids.Contains(busy.DriverId));
        Assert.IsFalse(ids.Contains(noConnect.DriverId));
        Assert.IsFalse(ids.Contains(stale.DriverId));
    }
}
