using System.Security.Claims;
using ApplicationTesting.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using RunDelta.API.Controllers;
using RunDelta.API.DTO;
using RunDelta.API.DTO.TenantBilling;
using RunDelta.API.Services.AccountManagement;

namespace ApplicationTesting.Billing;

/// <summary>
/// Exercises the production TenantBillingController payment-complete and repair
/// endpoints with the real service stack; only the authenticated-user -> tenant
/// resolution is substituted.
/// </summary>
[TestClass]
public sealed class PaymentCompleteEndpointTests
{
    private static TenantBillingController CreateController(BillingStack stack, int tenantId)
    {
        var userId = Guid.NewGuid();
        var account = new Mock<IAccountManagementService>(MockBehavior.Strict);
        account.Setup(x => x.GetTenantIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(tenantId);

        var controller = new TenantBillingController(account.Object, stack.StripeBilling, stack.Billing, stack.UnitOfWork)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"))
                }
            }
        };
        return controller;
    }

    private static async Task<(SeededTenant Seed, string IntentId)> CreatePendingAsync(BillingStack stack, string name)
    {
        var seed = await TenantSeeder.SeedAsync(stack.Context, name);
        var result = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId,
            new CreateMembershipPaymentIntentRequest { ProductId = seed.ProductId, PurchasedSeatCount = 2, TenantPaymentMethodId = seed.TenantPaymentMethodId });
        var row = await stack.Context.TenantBillingTransactions.AsNoTracking()
            .SingleAsync(x => x.TenantBillingTransactionId == result.TenantBillingTransactionId);
        stack.Context.ChangeTracker.Clear();
        return (seed, row.StripePaymentIntentId);
    }

    [TestMethod]
    public async Task PaymentComplete_Succeeded_FinalizesAndReturnsActiveStatus()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId) = await CreatePendingAsync(stack, "EpOk");
        stack.StripeHttp.SetPaymentIntent(intentId, "succeeded", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var response = await CreateController(stack, seed.TenantId)
            .CompleteMembershipPayment(new CompleteMembershipPaymentRequest { PaymentIntentId = intentId }, CancellationToken.None);

        Assert.IsInstanceOfType<OkObjectResult>(response);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(1, (await verify.TenantBillingTransactions.SingleAsync(x => x.StripePaymentIntentId == intentId)).Status);
        Assert.AreEqual(2, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync());
    }

    [TestMethod]
    public async Task PaymentComplete_CalledTwice_IsSafe()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId) = await CreatePendingAsync(stack, "EpTwice");
        stack.StripeHttp.SetPaymentIntent(intentId, "succeeded", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));
        var request = new CompleteMembershipPaymentRequest { PaymentIntentId = intentId };

        var first = await CreateController(stack, seed.TenantId).CompleteMembershipPayment(request, CancellationToken.None);
        var second = await CreateController(stack, seed.TenantId).CompleteMembershipPayment(request, CancellationToken.None);

        Assert.IsInstanceOfType<OkObjectResult>(first);
        Assert.IsInstanceOfType<OkObjectResult>(second);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(2, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync());
        Assert.AreEqual(1, await verify.TenantPlatformMemberships.CountAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task PaymentComplete_NotYetSucceeded_Returns409_WithoutFinalizing()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId) = await CreatePendingAsync(stack, "EpConflict");
        stack.StripeHttp.SetPaymentIntent(intentId, "requires_payment_method", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var response = await CreateController(stack, seed.TenantId)
            .CompleteMembershipPayment(new CompleteMembershipPaymentRequest { PaymentIntentId = intentId }, CancellationToken.None);

        Assert.IsInstanceOfType<ConflictObjectResult>(response);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(0, (await verify.TenantBillingTransactions.SingleAsync(x => x.StripePaymentIntentId == intentId)).Status);
    }

    [TestMethod]
    public async Task PaymentComplete_ForeignTenantIntent_Returns400_WithoutFinalizing()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (victim, intentId) = await CreatePendingAsync(stack, "EpVictim");
        var attacker = await TenantSeeder.SeedAsync(stack.Context, "EpAttacker");
        stack.StripeHttp.SetPaymentIntent(intentId, "succeeded", StripeEventFactory.MembershipMetadata(victim.TenantId, victim.ProductId));

        var response = await CreateController(stack, attacker.TenantId)
            .CompleteMembershipPayment(new CompleteMembershipPaymentRequest { PaymentIntentId = intentId }, CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(response);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(0, (await verify.TenantBillingTransactions.SingleAsync(x => x.StripePaymentIntentId == intentId)).Status);
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == attacker.TenantId || x.TenantId == victim.TenantId));
    }

    [TestMethod]
    public async Task PaymentComplete_Unauthenticated_Returns401()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var controller = new TenantBillingController(Mock.Of<IAccountManagementService>(), stack.StripeBilling, stack.Billing, stack.UnitOfWork)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = await controller.CompleteMembershipPayment(new CompleteMembershipPaymentRequest { PaymentIntentId = "pi_x" }, CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(response);
        Assert.AreEqual(0, stack.StripeHttp.Requests.Count);
    }

    [TestMethod]
    public async Task RepairEndpoint_ReturnsRepairResult_ForCurrentTenant()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId) = await CreatePendingAsync(stack, "EpRepair");
        stack.StripeHttp.SetPaymentIntent(intentId, "succeeded", StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId));

        var response = await CreateController(stack, seed.TenantId).RepairMembership(CancellationToken.None);

        var ok = response.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var result = (MembershipRepairResult)ok.Value!;
        Assert.AreEqual(seed.TenantId, result.TenantId);
        Assert.AreEqual(1, result.RepairedTransactionCount);
        Assert.AreEqual("Active", result.Billing.MembershipStatus);
    }
}
