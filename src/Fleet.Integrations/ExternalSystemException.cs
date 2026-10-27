using System.Text.Json.Nodes;

namespace Fleet.Integrations;

/// <summary>
/// Classified failure from a downstream system. Retryable failures (5xx, timeouts, rate limits) are left
/// for Temporal's retry policy. Non-retryable ones (validation errors) fail the activity immediately.
/// </summary>
public class ExternalSystemException : Exception
{
    public ExternalSystemException(string system, int statusCode, string? errorCode, string message, bool retryable)
        : base(message)
    {
        System = system;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Retryable = retryable;
    }

    public string System { get; }
    public int StatusCode { get; }
    public string? ErrorCode { get; }
    public bool Retryable { get; }

    public static ExternalSystemException FromResponse(string system, int status, string body)
    {
        string? code = null;
        string? message = null;
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray arr && arr.Count > 0)
            {
                // Salesforce style: [{ "message": "...", "errorCode": "..." }]
                code = arr[0]?["errorCode"]?.GetValue<string>();
                message = arr[0]?["message"]?.GetValue<string>();
            }
            else if (node is JsonObject obj)
            {
                code = obj["errorCode"]?.GetValue<string>() ?? obj["code"]?.GetValue<string>();
                message = obj["message"]?.GetValue<string>() ?? obj["error"]?.GetValue<string>();
            }
        }
        catch
        {
            // Body was not JSON; fall through with the raw text.
        }

        var retryableCodes = new[] { "REQUEST_LIMIT_EXCEEDED", "SERVER_UNAVAILABLE", "UNABLE_TO_LOCK_ROW" };
        var retryable = status >= 500 || status == 408 || status == 429 ||
                        (code != null && retryableCodes.Contains(code));
        var text = message ?? (string.IsNullOrWhiteSpace(body) ? "no body" : body.Length > 200 ? body[..200] : body);
        return new ExternalSystemException(system, status, code, $"{system} returned {status}{(code != null ? $" {code}" : "")}: {text}", retryable);
    }
}
