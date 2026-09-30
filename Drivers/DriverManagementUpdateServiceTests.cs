using Microsoft.AspNetCore.Identity;
using Moq;
using RunDelta.API.DTO;
using RunDelta.API.Identity.Persistence;
using RunDelta.Infrastructure.Models;

namespace ApplicationTesting.Drivers;

/// <summary>
/// Update-path orchestration for <c>DriverService.UpdateTenantDriverAsync</c>:
/// ownership check, Identity write ordering, Country validation, Home Address
/// create/update, and the Driver compatibility mirror. All dependencies are mocked.
/// </summary>
[TestClass]
public sealed class DriverManagementUpdateServiceTests
{
    private const int TenantId = 11;
    private const int DriverId = 42;
    private const int UserTenantId = 900;
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static (DriverManagementMocks m, Driver driver, User user) Arrange(
        string? identityFirst = "Old", string? identityLast = "Name", string? identityPhone = "+15550000000")
    {
        var m = new DriverManagementMocks();
        var driver = DriverManagementMocks.DriverRow(TenantId, DriverId, UserId, "Old", "Name", "+15550000000");
        var user = m.AddIdentityUser(UserId, "d@example.com", identityFirst, identityLast, identityPhone);

        m.Drivers.Setup(d => d.GetTenantDriverAsync(TenantId, DriverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(driver);
        m.Drivers.Setup(d => d.Update(driver));
        m.Drivers.Setup(d => d.GetTenantDriverForManagementAsync(
                TenantId, DriverId, DriverManagementMocks.HomeAddressTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => DriverManagementMocks.Record(
                TenantId, DriverId, UserId, driver.FirstName, driver.LastName, driver.PhoneNumber,
                userTenantId: UserTenantId));
        m.UnitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        m.SetupMembership(TenantId, null, 0, 0);

        m.UserManager.Setup(u => u.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        m.UserManager.Setup(u => u.SetPhoneNumberAsync(user, It.IsAny<string?>()))
            .ReturnsAsync(IdentityResult.Success)
            .Callback<User, string?>((usr, phone) =>
            {
                usr.PhoneNumber = phone;
                usr.PhoneNumberConfirmed = false;
            });

        return (m, driver, user);
    }

    [TestMethod]
    public async Task Update_NameChange_UpdatesIdentity_AndMirrorsDriverRow()
    {
        var (m, driver, user) = Arrange();

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest { FirstName = " New ", LastName = " Person " });

        Assert.IsTrue(result.Succeeded, string.Join(";", result.Errors));
        Assert.AreEqual("New", user.FirstName);
        Assert.AreEqual("Person", user.LastName);
        Assert.AreEqual("New", driver.FirstName);
        Assert.AreEqual("Person", driver.LastName);
        Assert.AreEqual("New", result.Value!.FirstName);
        Assert.AreEqual("Person", result.Value.LastName);

        m.UserManager.Verify(u => u.UpdateAsync(user), Times.Once);
        m.UserManager.Verify(u => u.SetPhoneNumberAsync(It.IsAny<User>(), It.IsAny<string?>()), Times.Never);
        m.Drivers.Verify(d => d.Update(driver), Times.Once);
        m.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Update_PhoneChange_UsesSetPhoneNumberAsync_AndResetsConfirmation()
    {
        var (m, driver, user) = Arrange();
        user.PhoneNumberConfirmed = true;

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest { PhoneNumber = "+15551234567" });

        Assert.IsTrue(result.Succeeded, string.Join(";", result.Errors));
        m.UserManager.Verify(u => u.SetPhoneNumberAsync(user, "+15551234567"), Times.Once);
        m.UserManager.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
        Assert.AreEqual("+15551234567", user.PhoneNumber);
        Assert.IsFalse(user.PhoneNumberConfirmed);
        Assert.AreEqual("+15551234567", driver.PhoneNumber);
        Assert.IsFalse(result.Value!.PhoneNumberConfirmed);
    }

    [TestMethod]
    public async Task Update_UnchangedValues_DoNotCallIdentity()
    {
        var (m, _, user) = Arrange("Old", "Name", "+15550000000");

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest { FirstName = "Old", LastName = "Name", PhoneNumber = "+15550000000" });

        Assert.IsTrue(result.Succeeded);
        m.UserManager.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
        m.UserManager.Verify(u => u.SetPhoneNumberAsync(It.IsAny<User>(), It.IsAny<string?>()), Times.Never);
    }

    [TestMethod]
    public async Task Update_IdentityFailure_ReturnsErrors_AndDoesNotSaveBusinessData()
    {
        var (m, _, user) = Arrange();
        m.UserManager.Setup(u => u.UpdateAsync(user))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "identity rejected" }));

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest { FirstName = "X" });

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.Contains(result.Errors.ToList(), "identity rejected");
        m.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Update_BusinessSaveFailure_AfterIdentity_IsReportedExplicitly()
    {
        var (m, _, _) = Arrange();
        m.UnitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest { FirstName = "X" });

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Errors.Any(e =>
            e.StartsWith("Identity was updated but Driver profile could not be saved")));
        m.UserManager.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Once);
    }

    [TestMethod]
    public async Task Update_ForeignOrUnknownDriver_FailsWithNotFoundConvention_WithoutWriting()
    {
        var m = new DriverManagementMocks();
        m.Drivers.Setup(d => d.GetTenantDriverAsync(TenantId, 999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Driver?)null);

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, 999,
            new UpdateTenantDriverRequest { FirstName = "Hacker" });

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("could not be resolved for this Tenant")));
        m.UserManager.Verify(u => u.FindByIdAsync(It.IsAny<string>()), Times.Never);
        m.UserManager.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
        m.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Update_OwnershipLookup_IsScopedByTenantAndDriver()
    {
        var (m, _, _) = Arrange();

        await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest { FirstName = "Scoped" });

        m.Drivers.Verify(d => d.GetTenantDriverAsync(TenantId, DriverId, It.IsAny<CancellationToken>()), Times.Once);
        m.Drivers.Verify(d => d.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(0, 5)]
    [DataRow(-1, 5)]
    [DataRow(5, 0)]
    public async Task Update_InvalidIds_FailFast(int tenantId, int driverId)
    {
        var m = new DriverManagementMocks();
        var result = await m.CreateService().UpdateTenantDriverAsync(tenantId, driverId,
            new UpdateTenantDriverRequest());

        Assert.IsFalse(result.Succeeded);
        m.Drivers.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Update_DriverWithoutUser_Fails()
    {
        var m = new DriverManagementMocks();
        m.Drivers.Setup(d => d.GetTenantDriverAsync(TenantId, DriverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DriverManagementMocks.DriverRow(TenantId, DriverId, null));

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest { FirstName = "X" });

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("not linked")));
    }

    [TestMethod]
    public async Task Update_HomeAddress_InvalidCountry_Fails_BeforeAnyWrite()
    {
        var (m, _, _) = Arrange();
        m.Countries.Setup(c => c.GetActiveByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Country?)null);

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest
            {
                FirstName = "ShouldNotApply",
                HomeAddress = new UpdateDriverAddressRequest
                    { Street1 = "1 A", City = "C", State = "S", PostalCode = "P", CountryId = 999 }
            });

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("country")));
        m.Addresses.Verify(a => a.AddAsync(It.IsAny<Address>(), It.IsAny<CancellationToken>()), Times.Never);
        m.UserManager.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
        m.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(null, "Austin", "TX", "78701", 1, "Street1")]
    [DataRow("10 Home Rd", " ", "TX", "78701", 1, "City")]
    [DataRow("10 Home Rd", "Austin", null, "78701", 1, "State")]
    [DataRow("10 Home Rd", "Austin", "TX", "", 1, "PostalCode")]
    [DataRow("10 Home Rd", "Austin", "TX", "78701", 0, "CountryId")]
    public async Task Update_HomeAddress_MissingRequiredField_Fails_BeforePersistence(
        string? street1, string? city, string? state, string? postalCode, int countryId, string expectedField)
    {
        var (m, _, _) = Arrange();

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest
            {
                HomeAddress = new UpdateDriverAddressRequest
                {
                    Street1 = street1, City = city, State = state,
                    PostalCode = postalCode, CountryId = countryId
                }
            });

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Errors.Any(e => e.Contains(expectedField)), string.Join(";", result.Errors));
        m.Countries.Verify(c => c.GetActiveByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        m.Addresses.Verify(a => a.GetUserTenantAddressAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        m.Addresses.Verify(a => a.AddAsync(It.IsAny<Address>(), It.IsAny<CancellationToken>()), Times.Never);
        m.UserManager.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
        m.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Update_HomeAddress_Missing_CreatesTypeFiveAddress_ForCorrectOwner()
    {
        var (m, _, _) = Arrange();
        m.Countries.Setup(c => c.GetActiveByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Country { CountryId = 1, IsActive = true });
        m.Addresses.Setup(a => a.GetUserTenantAddressAsync(
                UserTenantId, DriverManagementMocks.HomeAddressTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Address?)null);
        Address? added = null;
        m.Addresses.Setup(a => a.AddAsync(It.IsAny<Address>(), It.IsAny<CancellationToken>()))
            .Callback<Address, CancellationToken>((a, _) => added = a)
            .Returns(Task.CompletedTask);

        var before = DateTime.UtcNow.AddSeconds(-1);
        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest
            {
                HomeAddress = new UpdateDriverAddressRequest
                {
                    Street1 = "10 Home Rd", City = "Austin", State = "TX",
                    PostalCode = "78701", CountryId = 1, IsPrimary = true
                }
            });

        Assert.IsTrue(result.Succeeded, string.Join(";", result.Errors));
        Assert.IsNotNull(added);
        // CK_Addresses_Owner: exactly one owner. Home Address is owned by UserTenantId.
        Assert.IsNull(added!.TenantId);
        Assert.AreEqual(UserTenantId, added.UserTenantId);
        Assert.AreEqual(5, added.AddressTypeId);
        Assert.AreEqual(1, added.CountryId);
        Assert.AreEqual("10 Home Rd", added.Street1);
        Assert.AreEqual("Austin", added.City);
        Assert.AreEqual("TX", added.State);
        Assert.AreEqual("78701", added.PostalCode);
        Assert.AreEqual("Home", added.LocationName);
        Assert.IsTrue(added.IsPrimary);
        Assert.IsTrue(added.CreatedUtc >= before);
        Assert.IsNull(added.UpdatedUtc);
        m.Addresses.Verify(a => a.GetUserTenantAddressAsync(
            UserTenantId, DriverManagementMocks.HomeAddressTypeId, It.IsAny<CancellationToken>()), Times.Once);
        m.Addresses.Verify(a => a.Update(It.IsAny<Address>()), Times.Never);
    }

    [TestMethod]
    public async Task Update_HomeAddress_Existing_UpdatesInPlace_NoDuplicate()
    {
        var (m, _, _) = Arrange();
        var existing = new Address
        {
            AddressId = 77, TenantId = null, UserTenantId = UserTenantId,
            AddressTypeId = 5, Street1 = "Old St", City = "Old", State = "OS", PostalCode = "00000",
            CountryId = 1, CreatedUtc = DateTime.UtcNow.AddDays(-30)
        };
        m.Countries.Setup(c => c.GetActiveByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Country { CountryId = 2, IsActive = true });
        m.Addresses.Setup(a => a.GetUserTenantAddressAsync(
                UserTenantId, DriverManagementMocks.HomeAddressTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest
            {
                HomeAddress = new UpdateDriverAddressRequest
                    { AddressId = 77, Street1 = "New St", City = "X", State = "Y", PostalCode = "Z", CountryId = 2 }
            });

        Assert.IsTrue(result.Succeeded, string.Join(";", result.Errors));
        Assert.AreEqual("New St", existing.Street1);
        Assert.AreEqual(2, existing.CountryId);
        Assert.AreEqual(5, existing.AddressTypeId);
        Assert.IsNull(existing.TenantId);
        Assert.AreEqual(UserTenantId, existing.UserTenantId);
        Assert.AreEqual(77, existing.AddressId);
        Assert.IsNotNull(existing.UpdatedUtc);
        // Existing tracked row is mutated in place (EF change tracking);
        // no second Home row is inserted.
        m.Addresses.Verify(a => a.AddAsync(It.IsAny<Address>(), It.IsAny<CancellationToken>()), Times.Never);
        m.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Update_HomeAddress_MismatchedAddressId_IsRejected()
    {
        var (m, _, _) = Arrange();
        m.Countries.Setup(c => c.GetActiveByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Country { CountryId = 1, IsActive = true });
        m.Addresses.Setup(a => a.GetUserTenantAddressAsync(
                UserTenantId, DriverManagementMocks.HomeAddressTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Address { AddressId = 77, TenantId = null, UserTenantId = UserTenantId, AddressTypeId = 5 });

        var result = await m.CreateService().UpdateTenantDriverAsync(TenantId, DriverId,
            new UpdateTenantDriverRequest
            {
                HomeAddress = new UpdateDriverAddressRequest
                    { AddressId = 12345, Street1 = "x", City = "c", State = "s", PostalCode = "p", CountryId = 1 }
            });

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("AddressId")));
        m.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public void WriteContract_ExcludesServerControlledFields()
    {
        var writable = typeof(UpdateTenantDriverRequest).GetProperties().Select(p => p.Name).ToHashSet();

        CollectionAssert.AreEquivalent(
            new[] { "FirstName", "LastName", "PhoneNumber", "HomeAddress" },
            writable.ToList());

        foreach (var forbidden in new[]
                 {
                     "Email", "TenantId", "DriverId", "DriverUserId", "TenantPlatformMembershipId",
                     "ProductId", "PurchasedSeatCount", "AssignedSeatCount", "AvailableSeatCount",
                     "EffectiveFromUtc", "EffectiveThroughUtc", "DriverOnlineStatus", "DriverStatus",
                     "ConnectAccountId", "StripeConnectStatus", "EmailConfirmed", "PhoneNumberConfirmed"
                 })
        {
            Assert.IsFalse(writable.Contains(forbidden), $"{forbidden} must not be writable.");
        }

        var addressWritable = typeof(UpdateDriverAddressRequest).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.IsFalse(addressWritable.Contains("TenantId"));
        Assert.IsFalse(addressWritable.Contains("UserTenantId"));
        Assert.IsFalse(addressWritable.Contains("AddressTypeId"));
    }
}
