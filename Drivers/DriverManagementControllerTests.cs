using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using RunDelta.API.Controllers;
using RunDelta.API.DTO;
using RunDelta.API.Services.AccountManagement;
using RunDelta.API.Services.Interface;
using RunDelta.Repository.Helpers;
using RunDelta.Repository.UnitOfWork.Interface;

namespace ApplicationTesting.Drivers;

/// <summary>
/// Controller-level contract for the Provider management endpoints:
/// authorization metadata, tenant resolution, and Result→HTTP mapping.
/// Services are mocked; no SQL.
/// </summary>
[TestClass]
public sealed class DriverManagementControllerTests
{
    private static readonly Guid ProviderUserId = Guid.NewGuid();
    private const int TenantId = 5;

    private static (ManageDriversController controller,
        Mock<IDriverService> drivers,
        Mock<IAccountManagementService> accounts) Build(Guid? userId, int tenantId = TenantId)
    {
        var drivers = new Mock<IDriverService>(MockBehavior.Strict);
        var seats = new Mock<ITenantDriverSeatService>(MockBehavior.Strict);
        var accounts = new Mock<IAccountManagementService>(MockBehavior.Strict);
        var uow = new Mock<IUnitOfWork>(MockBehavior.Strict);

        if (userId is not null)
        {
            accounts.Setup(a => a.GetTenantIdAsync(userId.Value, It.IsAny<CancellationToken>()))
                .ReturnsAsync(tenantId);
        }

        var controller = new ManageDriversController(drivers.Object, seats.Object, accounts.Object, uow.Object);
        var claims = userId is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claims }
        };
        return (controller, drivers, accounts);
    }

    private static MethodInfo Action(string name) =>
        typeof(ManageDriversController).GetMethod(name)
        ?? throw new AssertFailedException($"Action {name} not found.");

    [TestMethod]
    public void Controller_RequiresAuthentication()
    {
        var auth = typeof(ManageDriversController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.IsNotNull(auth);
    }

    [DataTestMethod]
    [DataRow(nameof(ManageDriversController.GetTenantDriversForManagement), "GET", "tenant-drivers")]
    [DataRow(nameof(ManageDriversController.UpdateTenantDriver), "PUT", "tenant-drivers/{driverId:int}")]
    public void ManagementActions_RequireProviderRole_AndUseExpectedRoutes(
        string action, string verb, string template)
    {
        var method = Action(action);
        var auth = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.IsNotNull(auth, $"{action} must carry [Authorize].");
        Assert.AreEqual("Provider", auth!.Roles);

        var http = method.GetCustomAttributes<HttpMethodAttribute>().Single();
        CollectionAssert.Contains(http.HttpMethods.ToList(), verb);
        Assert.AreEqual(template, http.Template);
    }

    [TestMethod]
    public void ExistingActions_AreUnchanged()
    {
        foreach (var name in new[]
                 {
                     "AddNewDriver", "AddTenantDriver", "SuspendTenantDriver",
                     "ReinstateTenantDriver", "RemoveTenantDriver", "GetTenantForRideshare"
                 })
        {
            Assert.IsNotNull(typeof(ManageDriversController).GetMethod(name), $"{name} must still exist.");
        }
    }

    [TestMethod]
    public async Task Get_UnauthenticatedPrincipal_ReturnsBadRequest_WithoutCallingService()
    {
        var (controller, drivers, _) = Build(userId: null);

        var response = await controller.GetTenantDriversForManagement(CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(response);
        drivers.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Get_ResolvesTenantFromClaims_AndReturnsRows()
    {
        var (controller, drivers, accounts) = Build(ProviderUserId);
        var rows = new List<TenantDriverManagementDto> { new() { TenantId = TenantId, DriverId = 1 } };
        drivers.Setup(d => d.GetTenantDriversForManagementAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<TenantDriverManagementDto>>.Ok(rows));

        var response = await controller.GetTenantDriversForManagement(CancellationToken.None);

        var ok = response as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreSame(rows, ok!.Value);
        accounts.Verify(a => a.GetTenantIdAsync(ProviderUserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Get_UnresolvedTenant_ReturnsBadRequest()
    {
        var (controller, drivers, _) = Build(ProviderUserId, tenantId: 0);

        var response = await controller.GetTenantDriversForManagement(CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(response);
        drivers.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Get_ServiceFailure_ReturnsBadRequest()
    {
        var (controller, drivers, _) = Build(ProviderUserId);
        drivers.Setup(d => d.GetTenantDriversForManagementAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<TenantDriverManagementDto>>.Fail("nope"));

        var response = await controller.GetTenantDriversForManagement(CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(response);
    }

    [TestMethod]
    public async Task Put_PassesAuthenticatedTenant_NotClientSupplied()
    {
        var (controller, drivers, _) = Build(ProviderUserId);
        var request = new UpdateTenantDriverRequest { FirstName = "A" };
        drivers.Setup(d => d.UpdateTenantDriverAsync(TenantId, 7, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TenantDriverManagementDto>.Ok(new TenantDriverManagementDto { DriverId = 7 }));

        var response = await controller.UpdateTenantDriver(7, request, CancellationToken.None);

        Assert.IsInstanceOfType<OkObjectResult>(response);
        drivers.Verify(d => d.UpdateTenantDriverAsync(TenantId, 7, request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Put_ForeignTenantDriver_Returns404()
    {
        var (controller, drivers, _) = Build(ProviderUserId);
        var request = new UpdateTenantDriverRequest { FirstName = "A" };
        drivers.Setup(d => d.UpdateTenantDriverAsync(TenantId, 99, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TenantDriverManagementDto>.Fail("Driver could not be resolved for this Tenant."));

        var response = await controller.UpdateTenantDriver(99, request, CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundObjectResult>(response);
    }

    [TestMethod]
    public async Task Put_OtherServiceFailure_Returns400()
    {
        var (controller, drivers, _) = Build(ProviderUserId);
        var request = new UpdateTenantDriverRequest { FirstName = "A" };
        drivers.Setup(d => d.UpdateTenantDriverAsync(TenantId, 7, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TenantDriverManagementDto>.Fail("The selected country does not exist or is inactive."));

        var response = await controller.UpdateTenantDriver(7, request, CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(response);
    }

    [TestMethod]
    public async Task Put_InvalidDriverId_Returns400_WithoutTenantLookup()
    {
        var (controller, drivers, accounts) = Build(ProviderUserId);

        var response = await controller.UpdateTenantDriver(0, new UpdateTenantDriverRequest(), CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(response);
        drivers.VerifyNoOtherCalls();
        accounts.Verify(a => a.GetTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Put_NullBody_Returns400()
    {
        var (controller, drivers, _) = Build(ProviderUserId);

        var response = await controller.UpdateTenantDriver(7, null!, CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(response);
        drivers.VerifyNoOtherCalls();
    }
}
