using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Query;
using Moq;
using RunDelta.API.Identity.Persistence;
using RunDelta.API.Services.DriverService.RunDelta.API.Services;
using RunDelta.API.Services.Email.Interface;
using RunDelta.Globals.Records.Driver;
using RunDelta.Infrastructure.Models;
using RunDelta.Repository.Repositories.Interface;
using RunDelta.Repository.UnitOfWork.Interface;

namespace ApplicationTesting.Drivers;

/// <summary>
/// Mock-backed composition of <see cref="DriverService"/> for the Provider
/// driver-management contract. No SQL is involved; repository and Identity
/// behavior is stubbed per test. Follows the <c>Mock&lt;UserManager&lt;User&gt;&gt;</c>
/// convention used by <c>SendGridEmailServiceTests</c>.
/// </summary>
internal sealed class DriverManagementMocks
{
    public const int HomeAddressTypeId = 5;

    public Mock<IUnitOfWork> UnitOfWork { get; } = new(MockBehavior.Strict);
    public Mock<IDriverRepository> Drivers { get; } = new(MockBehavior.Strict);
    public Mock<IAddressRepository> Addresses { get; } = new(MockBehavior.Strict);
    public Mock<ICountryRepository> Countries { get; } = new(MockBehavior.Strict);
    public Mock<ITenantPlatformMembershipRepository> Memberships { get; } = new(MockBehavior.Strict);
    public Mock<ITenantProductRepository> Products { get; } = new(MockBehavior.Strict);
    public Mock<UserManager<User>> UserManager { get; }
    public Mock<IEmailService> Email { get; } = new(MockBehavior.Loose);

    public List<User> IdentityUsers { get; } = new();

    public DriverManagementMocks()
    {
        var store = new Mock<IUserStore<User>>();
        UserManager = new Mock<UserManager<User>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        UserManager.Setup(m => m.Users)
            .Returns(() => new AsyncEnumerableQuery<User>(IdentityUsers));

        UserManager.Setup(m => m.FindByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) =>
                IdentityUsers.FirstOrDefault(u => u.Id.ToString() == id));

        UnitOfWork.SetupGet(u => u.Drivers).Returns(Drivers.Object);
        UnitOfWork.SetupGet(u => u.Addresses).Returns(Addresses.Object);
        UnitOfWork.SetupGet(u => u.Countries).Returns(Countries.Object);
        UnitOfWork.SetupGet(u => u.TenantPlatformMemberships).Returns(Memberships.Object);
        UnitOfWork.SetupGet(u => u.TenantProducts).Returns(Products.Object);
    }

    public DriverService CreateService() =>
        new(UserManager.Object, UnitOfWork.Object, Email.Object);

    public void SetupMembership(
        int tenantId,
        TenantPlatformMembership? membership,
        int purchasedSeats,
        int assignedActiveDrivers)
    {
        Memberships.Setup(m => m.GetCurrentByTenantAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);

        if (membership is not null)
        {
            Products.Setup(p => p.GetCurrentActiveOperatingSeatCountAsync(
                    tenantId, membership.ProductId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(purchasedSeats);
        }

        Drivers.Setup(d => d.CountAsync(
                It.IsAny<Expression<Func<Driver, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(assignedActiveDrivers);
    }

    public void SetupManagementRecords(int tenantId, params TenantDriverManagementRecord[] records)
    {
        Drivers.Setup(d => d.GetTenantDriversForManagementAsync(
                tenantId, HomeAddressTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.ToList());
    }

    public static TenantDriverManagementRecord Record(
        int tenantId,
        int driverId,
        Guid userId,
        string? firstName = null,
        string? lastName = null,
        string? phone = null,
        int onlineStatus = 0,
        int driverStatus = 0,
        int? userTenantId = 100,
        TenantDriverHomeAddressRecord? home = null,
        TenantDriverVehicleRecord? vehicle = null) =>
        new()
        {
            TenantId = tenantId,
            DriverId = driverId,
            DriverUserId = userId,
            UserTenantId = userTenantId,
            FirstName = firstName,
            LastName = lastName,
            PhoneNumber = phone,
            OnlineStatus = onlineStatus,
            DriverStatus = driverStatus,
            HomeAddress = home,
            Vehicle = vehicle
        };

    public static Driver DriverRow(int tenantId, int driverId, Guid? userId,
        string first = "Row", string last = "Driver", string phone = "+10000000000") =>
        new()
        {
            Id = driverId,
            TenantId = tenantId,
            UserId = userId,
            FirstName = first,
            LastName = last,
            PhoneNumber = phone,
            ConnectAccountId = string.Empty
        };

    public User AddIdentityUser(Guid id, string email, string? first, string? last,
        string? phone, bool emailConfirmed = true, bool phoneConfirmed = true)
    {
        var user = new User
        {
            Id = id,
            UserName = email,
            Email = email,
            FirstName = first,
            LastName = last,
            PhoneNumber = phone,
            EmailConfirmed = emailConfirmed,
            PhoneNumberConfirmed = phoneConfirmed
        };
        IdentityUsers.Add(user);
        return user;
    }

    /// <summary>
    /// In-memory IQueryable that also implements IAsyncEnumerable so EF Core
    /// async extension methods (ToDictionaryAsync) work without a provider.
    /// </summary>
    private sealed class AsyncEnumerableQuery<T> : IQueryable<T>, IAsyncEnumerable<T>, IQueryProvider
    {
        private readonly IQueryable<T> _inner;

        public AsyncEnumerableQuery(IEnumerable<T> source) => _inner = source.AsQueryable();
        private AsyncEnumerableQuery(IQueryable<T> inner) => _inner = inner;

        public Type ElementType => _inner.ElementType;
        public Expression Expression => _inner.Expression;
        public IQueryProvider Provider => this;

        public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var item in _inner)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
            await Task.CompletedTask;
        }

        public IQueryable CreateQuery(Expression expression) =>
            new AsyncEnumerableQuery<T>(_inner.Provider.CreateQuery<T>(expression));

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new AsyncEnumerableQuery<TElement>(_inner.Provider.CreateQuery<TElement>(expression));

        public object? Execute(Expression expression) => _inner.Provider.Execute(expression);
        public TResult Execute<TResult>(Expression expression) => _inner.Provider.Execute<TResult>(expression);
    }
}
