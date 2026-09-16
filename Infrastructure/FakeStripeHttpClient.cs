using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Stripe;

namespace ApplicationTesting.Infrastructure;

/// <summary>
/// Records every outbound Stripe API request made by production code and
/// answers with deterministic JSON. This lets the harness observe the exact
/// wire-level amount, metadata, and idempotency key the SUT sends to Stripe.
/// </summary>
public sealed class FakeStripeHttpClient : IHttpClient
{
    public sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string? IdempotencyKey,
        IReadOnlyDictionary<string, string> Form);

    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentDictionary<string, string> _paymentIntentStatuses =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _paymentIntentMetadata =
        new(StringComparer.Ordinal);
    private static int _paymentIntentCounter;
    private static int _customerCounter;

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public IEnumerable<RecordedRequest> PaymentIntentCreates =>
        Requests.Where(r => r.Method == HttpMethod.Post && r.Path == "/v1/payment_intents");

    public IEnumerable<RecordedRequest> CustomerCreates =>
        Requests.Where(r => r.Method == HttpMethod.Post && r.Path == "/v1/customers");

    public string? FixedPaymentIntentId { get; set; }

    public string? FixedClientSecret { get; set; }

    public string? LatestChargeId { get; set; } = "ch_test_latest";

    /// <summary>
    /// Registers a PaymentIntent that GET /v1/payment_intents/{id} will return
    /// with the given status and metadata (used for reconciliation/repair paths).
    /// </summary>
    public void SetPaymentIntent(
        string id,
        string status,
        IDictionary<string, string>? metadata = null,
        string? clientSecret = null)
    {
        _paymentIntentStatuses[id] = status;
        _paymentIntentMetadata[id] = metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        if (clientSecret is not null)
        {
            _paymentIntentMetadata[id]["__client_secret"] = clientSecret;
        }
    }

    public Task<StripeResponse> MakeRequestAsync(
        StripeRequest request,
        CancellationToken cancellationToken = default)
    {
        var form = ParseForm(request);
        request.StripeHeaders.TryGetValue("Idempotency-Key", out var idempotencyKey);
        var path = request.Uri.AbsolutePath;

        _requests.Enqueue(new RecordedRequest(request.Method, path, idempotencyKey, form));

        var json = Route(request.Method, path, form);
        var response = new StripeResponse(
            HttpStatusCode.OK,
            new HttpResponseMessage().Headers,
            json);

        return Task.FromResult(response);
    }

    public Task<StripeStreamedResponse> MakeStreamingRequestAsync(
        StripeRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming is not used by the billing SUT.");
    }

    private string Route(HttpMethod method, string path, IReadOnlyDictionary<string, string> form)
    {
        if (method == HttpMethod.Post && path == "/v1/customers")
        {
            var id = $"cus_test_{Interlocked.Increment(ref _customerCounter):D6}_{Guid.NewGuid():N}"[..40];
            return Json(new { id, @object = "customer", metadata = ExtractMetadata(form) });
        }

        if (method == HttpMethod.Post && path == "/v1/payment_intents")
        {
            var id = FixedPaymentIntentId
                ?? $"pi_test_{Interlocked.Increment(ref _paymentIntentCounter):D6}_{Guid.NewGuid():N}"[..40];
            var metadata = ExtractMetadata(form);
            var clientSecret = FixedClientSecret ?? $"{id}_secret_test";
            _paymentIntentStatuses.TryAdd(id, "requires_payment_method");
            _paymentIntentMetadata.TryAdd(id, metadata);
            _paymentIntentMetadata[id]["__client_secret"] = clientSecret;
            var amount = long.Parse(form["amount"]);
            return PaymentIntentJson(id, "requires_payment_method", amount, form["currency"], metadata, clientSecret);
        }

        if (path.StartsWith("/v1/payment_intents/", StringComparison.Ordinal))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var id = segments[2];
            var isCancel = segments.Length > 3 && segments[3] == "cancel";
            if (isCancel)
            {
                _paymentIntentStatuses[id] = "canceled";
            }

            var status = _paymentIntentStatuses.TryGetValue(id, out var s) ? s : "requires_payment_method";
            var metadata = _paymentIntentMetadata.TryGetValue(id, out var m)
                ? new Dictionary<string, string>(m, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            metadata.Remove("__client_secret", out var secret);
            return PaymentIntentJson(id, status, 0, "usd", metadata, secret ?? $"{id}_secret_test");
        }

        throw new InvalidOperationException($"Unexpected Stripe request {method} {path} in verification harness.");
    }

    private string PaymentIntentJson(
        string id,
        string status,
        long amount,
        string currency,
        Dictionary<string, string> metadata,
        string clientSecret)
    {
        return Json(new
        {
            id,
            @object = "payment_intent",
            status,
            amount,
            currency,
            client_secret = clientSecret,
            latest_charge = status == "succeeded" ? LatestChargeId : null,
            metadata
        });
    }

    private static Dictionary<string, string> ExtractMetadata(IReadOnlyDictionary<string, string> form)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in form)
        {
            if (key.StartsWith("metadata[", StringComparison.Ordinal) && key.EndsWith(']'))
            {
                metadata[key[9..^1]] = value;
            }
        }

        return metadata;
    }

    private static Dictionary<string, string> ParseForm(StripeRequest request)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Content is null)
        {
            return result;
        }

        var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(idx < 0 ? pair : pair[..idx]);
            var value = idx < 0 ? string.Empty : Uri.UnescapeDataString(pair[(idx + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
