using RunDelta.Globals.Enums;
using RunDelta.Infrastructure.Models;

namespace ApplicationTesting.Infrastructure;

public sealed record SeededDriver(
    int DriverId,
    Guid UserId,
    int TenantId);

/// <summary>
/// Seeds a fully ride-eligible provider: Tenant, UserTenant, active
/// TenantMembership, Driver (Active/Online/Available/None, Connect account),
/// DriverSettings and a DriverLocation. Each call creates its own Tenant so
/// tests can prove discovery is not partitioned by TenantId.
/// </summary>
public static class DriverSeeder
{
    public static async Task<SeededDriver> SeedAsync(
        RunDelta_DbContext context,
        string name,
        double latitude,
        double longitude,
        decimal baseFare = 3m,
        decimal perMileRate = 2m,
        decimal chargePerKm = 1.25m,
        double radiusMeters = 8_000,
        decimal cancellationFee = 5m,
        ChargePerMileOrKmType unit = ChargePerMileOrKmType.PerMile,
        DriverOnlineStatus online = DriverOnlineStatus.GoOnline,
        DriverAvailabilityStatus availability = DriverAvailabilityStatus.Available,
        bool activeMembership = true,
        bool withConnectAccount = true,
        bool withLocation = true,
        DateTimeOffset? locationUpdatedAt = null,
        int? tenantId = null,
        string? tenantConnectAccountId = null)
    {
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        if (tenantId is null)
        {
            var tenant = new Tenant
            {
                Name = $"{name} Tenant",
                IsActive = true,
                CreatedUtc = DateTime.UtcNow,
                UserId = Guid.NewGuid(),
                ConnectAccountId = tenantConnectAccountId ?? $"acct_{Guid.NewGuid():N}"[..21]
            };
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
            tenantId = tenant.Id;
        }

        context.UserTenants.Add(new UserTenant
        {
            UserId = userId,
            TenantId = tenantId.Value,
            CreatedAt = DateTime.UtcNow
        });

        context.TenantMemberships.Add(new TenantMembership
        {
            TenantId = tenantId.Value,
            UserId = userId,
            Role = (int)TenantMembershipRole.Contractor,
            IsActive = activeMembership,
            CreatedUtc = DateTime.UtcNow
        });

        var driver = new Driver
        {
            TenantId = tenantId.Value,
            UserId = userId,
            FirstName = name,
            LastName = "Driver",
            PhoneNumber = "5555550100",
            DriverStatus = (int)DriverStatus.Active,
            OnlineStatus = (int)online,
            AvailabilityStatus = (int)availability,
            RideStatus = (int)DriverRideStatus.None,
            ConnectAccountId = withConnectAccount ? $"acct_{Guid.NewGuid():N}"[..21] : null!,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.Drivers.Add(driver);
        await context.SaveChangesAsync();

        context.DriverSettings.Add(new DriverSetting
        {
            DriverId = driver.Id,
            BaseFare = baseFare,
            PerMileRate = perMileRate,
            ChargePerKm = chargePerKm,
            RadiusMeters = radiusMeters,
            CancellationFee = cancellationFee,
            ChargePerMileOrKmType = (int)unit,
            UsePlatformGeoVoice = false
        });

        if (withLocation)
        {
            context.DriverLocations.Add(new DriverLocation
            {
                DriverId = driver.Id,
                Latitude = latitude,
                Longitude = longitude,
                UpdatedAt = locationUpdatedAt ?? now
            });
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return new SeededDriver(driver.Id, userId, tenantId.Value);
    }
}
