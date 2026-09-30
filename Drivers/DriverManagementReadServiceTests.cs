using Moq;
using RunDelta.API.DTO;
using RunDelta.Globals.Enums;
using RunDelta.Globals.Records.Driver;
using RunDelta.Infrastructure.Models;

namespace ApplicationTesting.Drivers;

/// <summary>
/// Read-path orchestration and DTO mapping for
/// <c>DriverService.GetTenantDriversForManagementAsync</c>. Repository and
/// Identity are mocked; SQL projection behavior is covered separately by
/// repository integration tests.
/// </summary>
[TestClass]
public sealed class DriverManagementReadServiceTests
{
    private const int TenantId = 7;

    [TestMethod]
    public async Task Get_InvalidTenant_Fails_WithoutTouchingRepositories()
    {
        var m = new DriverManagementMocks();
        var result = await m.CreateService().GetTenantDriversForManagementAsync(0);

        Assert.IsFalse(result.Succeeded);
        m.Drivers.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Get_IdentityValues_WinOverDriverMirror()
    {
        var m = new DriverManagementMocks();
        var userId = Guid.NewGuid();
        m.AddIdentityUser(userId, "id@example.com", "IdentityFirst", "IdentityLast",
            "+15550001111", emailConfirmed: true, phoneConfirmed: false);
        m.SetupManagementRecords(TenantId,
            DriverManagementMocks.Record(TenantId, 1, userId, "RowFirst", "RowLast", "+19999999999"));
        m.SetupMembership(TenantId, null, 0, 0);

        var result = await m.CreateService().GetTenantDriversForManagementAsync(TenantId);

        Assert.IsTrue(result.Succeeded, string.Join(";", result.Errors));
        var dto = result.Value!.Single();
        Assert.AreEqual("IdentityFirst", dto.FirstName);
        Assert.AreEqual("IdentityLast", dto.LastName);
        Assert.AreEqual("id@example.com", dto.Email);
        Assert.AreEqual("+15550001111", dto.PhoneNumber);
        Assert.IsTrue(dto.EmailConfirmed);
        Assert.IsFalse(dto.PhoneNumberConfirmed);
        Assert.AreEqual(userId, dto.DriverUserId);
    }

    [TestMethod]
    public async Task Get_InvitedDriver_FallsBackToDriverRow_WhenIdentityEmpty()
    {
        var m = new DriverManagementMocks();
        var userId = Guid.NewGuid();
        m.AddIdentityUser(userId, "invited@example.com", null, null, null,
            emailConfirmed: false, phoneConfirmed: false);
        m.SetupManagementRecords(TenantId,
            DriverManagementMocks.Record(TenantId, 2, userId, "Invited", "Person", "+15550002222"));
        m.SetupMembership(TenantId, null, 0, 0);

        var dto = (await m.CreateService().GetTenantDriversForManagementAsync(TenantId)).Value!.Single();

        Assert.AreEqual("Invited", dto.FirstName);
        Assert.AreEqual("Person", dto.LastName);
        Assert.AreEqual("+15550002222", dto.PhoneNumber);
        Assert.AreEqual("invited@example.com", dto.Email);
    }

    [TestMethod]
    public async Task Get_UsesSingleIdentityQuery_ForAllDrivers()
    {
        var m = new DriverManagementMocks();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            m.AddIdentityUser(ids[i], $"u{i}@example.com", $"F{i}", $"L{i}", null);
        }
        m.SetupManagementRecords(TenantId,
            ids.Select((id, i) => DriverManagementMocks.Record(TenantId, i + 1, id)).ToArray());
        m.SetupMembership(TenantId, null, 0, 0);

        var result = await m.CreateService().GetTenantDriversForManagementAsync(TenantId);

        Assert.AreEqual(5, result.Value!.Count);
        m.UserManager.Verify(u => u.Users, Times.Once);
        m.UserManager.Verify(u => u.FindByIdAsync(It.IsAny<string>()), Times.Never);
        m.Drivers.Verify(d => d.GetTenantDriversForManagementAsync(
            TenantId, DriverManagementMocks.HomeAddressTypeId, It.IsAny<CancellationToken>()), Times.Once);
        m.Memberships.Verify(x => x.GetCurrentByTenantAsync(TenantId, It.IsAny<CancellationToken>()), Times.Once);
        m.Addresses.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Get_HomeAddress_IsMappedFromProjection()
    {
        var m = new DriverManagementMocks();
        var userId = Guid.NewGuid();
        m.AddIdentityUser(userId, "h@example.com", "H", "A", null);
        var home = new TenantDriverHomeAddressRecord
        {
            AddressId = 55, AddressTypeId = 5, LocationName = "Home",
            Street1 = "1 Main", Street2 = "Apt 2", City = "Austin", State = "TX",
            PostalCode = "78701", CountryId = 1, IsPrimary = true
        };
        m.SetupManagementRecords(TenantId,
            DriverManagementMocks.Record(TenantId, 3, userId, home: home));
        m.SetupMembership(TenantId, null, 0, 0);

        var dto = (await m.CreateService().GetTenantDriversForManagementAsync(TenantId)).Value!.Single();

        Assert.IsNotNull(dto.HomeAddress);
        Assert.AreEqual(55, dto.HomeAddress!.AddressId);
        Assert.AreEqual(5, dto.HomeAddress.AddressTypeId);
        Assert.AreEqual("Home", dto.HomeAddress.LocationName);
        Assert.AreEqual("1 Main", dto.HomeAddress.Street1);
        Assert.AreEqual("Apt 2", dto.HomeAddress.Street2);
        Assert.AreEqual("Austin", dto.HomeAddress.City);
        Assert.AreEqual("TX", dto.HomeAddress.State);
        Assert.AreEqual("78701", dto.HomeAddress.PostalCode);
        Assert.AreEqual(1, dto.HomeAddress.CountryId);
        Assert.IsTrue(dto.HomeAddress.IsPrimary);
    }

    [TestMethod]
    public async Task Get_MembershipSnapshot_MatchesSeatServiceSources()
    {
        var m = new DriverManagementMocks();
        var userId = Guid.NewGuid();
        m.AddIdentityUser(userId, "m@example.com", "M", "S", null);
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var thru = from.AddYears(1);
        m.SetupManagementRecords(TenantId,
            DriverManagementMocks.Record(TenantId, 4, userId, driverStatus: (int)DriverStatus.Suspended));
        m.SetupMembership(TenantId,
            new TenantPlatformMembership
            {
                TenantPlatformMembershipId = 31, TenantId = TenantId, ProductId = 9,
                EffectiveFromUtc = from, EffectiveThroughUtc = thru
            },
            purchasedSeats: 10, assignedActiveDrivers: 4);

        var dto = (await m.CreateService().GetTenantDriversForManagementAsync(TenantId)).Value!.Single();

        Assert.AreEqual(TenantId, dto.TenantId);
        Assert.AreEqual(4, dto.DriverId);
        Assert.AreEqual((int)DriverStatus.Suspended, dto.DriverStatus);
        Assert.AreEqual(31, dto.TenantPlatformMembershipId);
        Assert.AreEqual(9, dto.ProductId);
        Assert.AreEqual(10, dto.PurchasedSeatCount);
        Assert.AreEqual(4, dto.AssignedSeatCount);
        Assert.AreEqual(6, dto.AvailableSeatCount);
        Assert.AreEqual(from, dto.EffectiveFromUtc);
        Assert.AreEqual(thru, dto.EffectiveThroughUtc);

        m.Products.Verify(p => p.GetCurrentActiveOperatingSeatCountAsync(
            TenantId, 9, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Get_NoMembership_ReportsZeroSeats_NeverNegativeAvailable()
    {
        var m = new DriverManagementMocks();
        var userId = Guid.NewGuid();
        m.AddIdentityUser(userId, "z@example.com", "Z", "Z", null);
        m.SetupManagementRecords(TenantId, DriverManagementMocks.Record(TenantId, 5, userId));
        m.SetupMembership(TenantId, null, 0, assignedActiveDrivers: 3);

        var dto = (await m.CreateService().GetTenantDriversForManagementAsync(TenantId)).Value!.Single();

        Assert.AreEqual(0, dto.TenantPlatformMembershipId);
        Assert.AreEqual(0, dto.PurchasedSeatCount);
        Assert.AreEqual(3, dto.AssignedSeatCount);
        Assert.AreEqual(0, dto.AvailableSeatCount);
        Assert.IsNull(dto.EffectiveFromUtc);
        m.Products.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow(0, DriverOnlineStatus.GoOffline)]
    [DataRow(1, DriverOnlineStatus.GoOnline)]
    [DataRow(2, DriverOnlineStatus.Paused)]
    public async Task Get_ReturnsActualPresenceEnum(int stored, DriverOnlineStatus expected)
    {
        var m = new DriverManagementMocks();
        var userId = Guid.NewGuid();
        m.AddIdentityUser(userId, "p@example.com", "P", "P", null);
        m.SetupManagementRecords(TenantId,
            DriverManagementMocks.Record(TenantId, 6, userId, onlineStatus: stored));
        m.SetupMembership(TenantId, null, 0, 0);

        var dto = (await m.CreateService().GetTenantDriversForManagementAsync(TenantId)).Value!.Single();

        Assert.AreEqual(expected, dto.DriverOnlineStatus);
        Assert.IsNull(typeof(TenantDriverManagementDto).GetProperty("IsActive"));
    }

    [TestMethod]
    public async Task Get_VehicleSummary_IsMapped_OrNullWhenUnassigned()
    {
        var m = new DriverManagementMocks();
        var withVehicle = Guid.NewGuid();
        var without = Guid.NewGuid();
        m.AddIdentityUser(withVehicle, "v1@example.com", "V", "One", null);
        m.AddIdentityUser(without, "v2@example.com", "V", "Two", null);
        m.SetupManagementRecords(TenantId,
            DriverManagementMocks.Record(TenantId, 8, withVehicle, vehicle: new TenantDriverVehicleRecord
            {
                VehicleId = 12, Make = "Toyota", Model = "Camry", LicensePlate = "ABC123",
                AssignmentStatus = (int)VehicleAssignmentStatus.ASSIGNED
            }),
            DriverManagementMocks.Record(TenantId, 9, without));
        m.SetupMembership(TenantId, null, 0, 0);

        var rows = (await m.CreateService().GetTenantDriversForManagementAsync(TenantId)).Value!;

        var v = rows.Single(r => r.DriverId == 8).Vehicle;
        Assert.IsNotNull(v);
        Assert.AreEqual(12, v!.VehicleId);
        Assert.AreEqual("Toyota", v.Make);
        Assert.AreEqual("Camry", v.Model);
        Assert.AreEqual("ABC123", v.LicensePlate);
        Assert.AreEqual(VehicleAssignmentStatus.ASSIGNED, v.AssignmentStatus);
        Assert.IsNull(rows.Single(r => r.DriverId == 9).Vehicle);
    }

    [TestMethod]
    public async Task Get_MissingIdentityUser_StillReturnsRow_WithDriverFallback()
    {
        var m = new DriverManagementMocks();
        var orphan = Guid.NewGuid();
        m.SetupManagementRecords(TenantId,
            DriverManagementMocks.Record(TenantId, 10, orphan, "Orphan", "Row", "+15550003333"));
        m.SetupMembership(TenantId, null, 0, 0);

        var dto = (await m.CreateService().GetTenantDriversForManagementAsync(TenantId)).Value!.Single();

        Assert.AreEqual("Orphan", dto.FirstName);
        Assert.AreEqual(string.Empty, dto.Email);
        Assert.IsFalse(dto.EmailConfirmed);
    }

    [TestMethod]
    public async Task Get_RepositoryFailure_ReturnsFail_NotThrow()
    {
        var m = new DriverManagementMocks();
        m.Drivers.Setup(d => d.GetTenantDriversForManagementAsync(
                TenantId, DriverManagementMocks.HomeAddressTypeId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await m.CreateService().GetTenantDriversForManagementAsync(TenantId);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("boom")));
    }
}
