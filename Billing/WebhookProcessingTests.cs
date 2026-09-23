using ApplicationTesting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RunDelta.API.Configuration;
using RunDelta.API.DTO.TenantBilling;
using RunDelta.API.Services.Interface;
using RunDelta.API.Services.PlatformBilling.Interface;
using RunDelta.API.Services.StripeConnect;

namespace ApplicationTesting.Billing;

[TestClass]
public sealed class WebhookProcessingTests
{
    private static StripeConnectWebhookService CreateWebhook(
        BillingStack stack,
        ITenantBillingService? billing = null,
        bool enabled = true)
    {
        return new StripeConnectWebhookService(
            stack.StripeClient,
            new StripeOptions
            {
                WebhookEnabled = enabled,
                WebhookSecret = StripeEventFactory.WebhookSecret
            },
            stack.UnitOfWork,
            Mock.Of<IStripeConnectService>(),
            billing ?? stack.Billing,
            Mock.Of<RunDelta.API.Services.Ride.Payments.IRidePaymentService>(),
            NullLogger<StripeConnectWebhookService>.Instance);
    }

    private static async Task<(SeededTenant Seed, string IntentId, string Reference)> CreatePendingAsync(BillingStack stack, string name)
    {
        var seed = await TenantSeeder.SeedAsync(stack.Context, name);
        var result = await stack.Billing.CreateMembershipPaymentIntentAsync(seed.TenantId,
            new CreateMembershipPaymentIntentRequest { ProductId = seed.ProductId, PurchasedSeatCount = 2, TenantPaymentMethodId = seed.TenantPaymentMethodId });
        var row = await stack.Context.TenantBillingTransactions.AsNoTracking()
            .SingleAsync(x => x.TenantBillingTransactionId == result.TenantBillingTransactionId);
        stack.Context.ChangeTracker.Clear();
        return (seed, row.StripePaymentIntentId, row.PurchaseReference);
    }

    [TestMethod]
    public async Task Webhook_RejectsInvalidSignature_WithoutTouchingBilling()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var billing = new Mock<ITenantBillingService>(MockBehavior.Strict);
        var webhook = CreateWebhook(stack, billing.Object);
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.succeeded", "pi_x", "succeeded",
            StripeEventFactory.MembershipMetadata(1, 1));

        await Assert.ThrowsExactlyAsync<StripeWebhookValidationException>(() =>
            webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload, secret: "whsec_wrong_secret")));
        await Assert.ThrowsExactlyAsync<StripeWebhookValidationException>(() =>
            webhook.ProcessAsync(payload, "t=1,v1=deadbeef"));
        await Assert.ThrowsExactlyAsync<StripeWebhookValidationException>(() =>
            webhook.ProcessAsync(payload, string.Empty));

        billing.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Webhook_RejectsTamperedBody()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var billing = new Mock<ITenantBillingService>(MockBehavior.Strict);
        var webhook = CreateWebhook(stack, billing.Object);
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.succeeded", "pi_x", "succeeded",
            StripeEventFactory.MembershipMetadata(1, 1));
        var signature = StripeEventFactory.Sign(payload);
        var tampered = payload.Replace("\"RunDeltaTenantId\":\"1\"", "\"RunDeltaTenantId\":\"2\"", StringComparison.Ordinal);
        Assert.AreNotEqual(payload, tampered);

        await Assert.ThrowsExactlyAsync<StripeWebhookValidationException>(() => webhook.ProcessAsync(tampered, signature));
        billing.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Webhook_RejectsStaleTimestamp()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var billing = new Mock<ITenantBillingService>(MockBehavior.Strict);
        var webhook = CreateWebhook(stack, billing.Object);
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.succeeded", "pi_x", "succeeded",
            StripeEventFactory.MembershipMetadata(1, 1));
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();

        await Assert.ThrowsExactlyAsync<StripeWebhookValidationException>(() =>
            webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload, timestamp: stale)));
        billing.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Webhook_Disabled_ReturnsUnavailable()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var webhook = CreateWebhook(stack, enabled: false);
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.succeeded", "pi_x", "succeeded",
            StripeEventFactory.MembershipMetadata(1, 1));

        await Assert.ThrowsExactlyAsync<StripeWebhookUnavailableException>(() =>
            webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload)));
    }

    [TestMethod]
    public async Task Webhook_PaymentIntentSucceeded_FinalizesMembership()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, reference) = await CreatePendingAsync(stack, "HookOk");
        var webhook = CreateWebhook(stack);
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.succeeded", intentId, "succeeded",
            StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId, reference), latestChargeId: "ch_hook_ok");

        var result = await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));

        Assert.IsTrue(result.Handled);
        Assert.AreEqual("payment_intent.succeeded", result.EventType);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        var row = await verify.TenantBillingTransactions.SingleAsync(x => x.StripePaymentIntentId == intentId);
        Assert.AreEqual(1, row.Status);
        Assert.AreEqual("ch_hook_ok", row.StripeChargeId);
        Assert.AreEqual(2, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync());
        Assert.IsTrue(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == seed.TenantId && x.Status == 3));
    }

    [TestMethod]
    public async Task Webhook_DuplicateDelivery_DoesNotDoubleApply()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, reference) = await CreatePendingAsync(stack, "HookDup");
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.succeeded", intentId, "succeeded",
            StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId, reference), eventId: "evt_dup_fixed");
        var signature = StripeEventFactory.Sign(payload);

        await CreateWebhook(stack).ProcessAsync(payload, signature);
        await using var redelivery = BillingStack.Create(VerificationDatabase.Instance);
        await CreateWebhook(redelivery).ProcessAsync(payload, signature);

        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(2, await verify.TenantProducts.Where(x => x.ProductId == seed.ProductId).Select(x => x.Quantity).SingleAsync());
        Assert.AreEqual(1, await verify.TenantPlatformMemberships.CountAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task Webhook_PaymentFailed_LeavesPending()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (seed, intentId, reference) = await CreatePendingAsync(stack, "HookFail");
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.payment_failed", intentId, "requires_payment_method",
            StripeEventFactory.MembershipMetadata(seed.TenantId, seed.ProductId, reference), latestChargeId: null);

        var result = await CreateWebhook(stack).ProcessAsync(payload, StripeEventFactory.Sign(payload));

        Assert.IsTrue(result.Handled);
        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(0, (await verify.TenantBillingTransactions.SingleAsync(x => x.StripePaymentIntentId == intentId)).Status);
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == seed.TenantId));
    }

    [TestMethod]
    public async Task Webhook_NonMembershipPaymentIntent_IsIgnored()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var billing = new Mock<ITenantBillingService>(MockBehavior.Strict);
        var webhook = CreateWebhook(stack, billing.Object);
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.succeeded", "pi_ride", "succeeded",
            new Dictionary<string, string> { ["RunDeltaTenantId"] = "1", ["Purpose"] = "RidePayment" });

        var result = await webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload));

        Assert.IsFalse(result.Handled);
        billing.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Webhook_MembershipIntent_MissingTenantMetadata_IsRejected()
    {
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var billing = new Mock<ITenantBillingService>(MockBehavior.Strict);
        var webhook = CreateWebhook(stack, billing.Object);
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.succeeded", "pi_bad", "succeeded",
            new Dictionary<string, string> { ["Purpose"] = "TenantPlatformMembership", ["RunDeltaTenantId"] = "not-a-number" });

        await Assert.ThrowsExactlyAsync<StripeWebhookValidationException>(() =>
            webhook.ProcessAsync(payload, StripeEventFactory.Sign(payload)));
        billing.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Webhook_ForgedTenantMetadata_CannotFinalizeAnotherTenantsIntent()
    {
        // Attacker-controlled metadata cannot pass signature verification in production,
        // but even a legitimately signed event whose tenant does not own the intent must fail.
        await using var stack = BillingStack.Create(VerificationDatabase.Instance);
        var (victim, intentId, reference) = await CreatePendingAsync(stack, "HookVictim");
        var other = await TenantSeeder.SeedAsync(stack.Context, "HookOther");
        var payload = StripeEventFactory.PaymentIntentEvent("payment_intent.succeeded", intentId, "succeeded",
            StripeEventFactory.MembershipMetadata(other.TenantId, other.ProductId, reference));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CreateWebhook(stack).ProcessAsync(payload, StripeEventFactory.Sign(payload)));

        await using var verify = VerificationDatabase.Instance.CreateContext();
        Assert.AreEqual(0, (await verify.TenantBillingTransactions.SingleAsync(x => x.StripePaymentIntentId == intentId)).Status);
        Assert.IsFalse(await verify.TenantPlatformMemberships.AnyAsync(x => x.TenantId == other.TenantId || x.TenantId == victim.TenantId));
    }
}
