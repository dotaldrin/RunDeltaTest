using System.Text.Json;
using Stripe;

namespace ApplicationTesting.Infrastructure;

/// <summary>
/// Builds Stripe webhook payloads and signature headers exactly as Stripe would
/// deliver them, so the production webhook service is exercised end-to-end.
/// </summary>
public static class StripeEventFactory
{
    public const string WebhookSecret = "whsec_applicationtesting_secret_0123456789";

    public static string PaymentIntentEvent(
        string eventType,
        string paymentIntentId,
        string status,
        IDictionary<string, string> metadata,
        string? latestChargeId = "ch_test_webhook",
        string? eventId = null)
    {
        var payload = new
        {
            id = eventId ?? $"evt_test_{Guid.NewGuid():N}",
            @object = "event",
            api_version = "2025-01-27.acacia",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            type = eventType,
            livemode = false,
            pending_webhooks = 1,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            data = new
            {
                @object = new
                {
                    id = paymentIntentId,
                    @object = "payment_intent",
                    status,
                    amount = 4500,
                    currency = "usd",
                    latest_charge = latestChargeId,
                    metadata
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    public static Dictionary<string, string> MembershipMetadata(
        int tenantId,
        int productId,
        string purchaseReference = "ref")
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RunDeltaTenantId"] = tenantId.ToString(),
            ["ProductId"] = productId.ToString(),
            ["Purpose"] = "TenantPlatformMembership",
            ["BillingMethod"] = "Upfront",
            ["PurchaseReference"] = purchaseReference
        };
    }

    public static string Sign(string payload, string? secret = null, long? timestamp = null)
    {
        return EventUtility.GenerateSignatureHeader(
            payload,
            secret ?? WebhookSecret,
            timestamp);
    }
}
