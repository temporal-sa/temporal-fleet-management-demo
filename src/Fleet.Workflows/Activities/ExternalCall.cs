using Fleet.Integrations;
using Temporalio.Exceptions;

namespace Fleet.Workflows.Activities;

/// <summary>
/// Translates downstream failures into Temporal semantics. Retryable problems (5xx, timeouts, rate limits)
/// are rethrown so the activity retry policy handles them. Permanent problems (validation, auth) fail fast.
/// </summary>
internal static class ExternalCall
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ExternalSystemException e) when (!e.Retryable)
        {
            throw new ApplicationFailureException(e.Message, errorType: $"{Clean(e.System)}Rejected", nonRetryable: true);
        }
        catch (ExternalSystemException e)
        {
            throw new ApplicationFailureException(e.Message, errorType: $"{Clean(e.System)}Unavailable");
        }
    }

    public static Task RunAsync(Func<Task> call) => RunAsync(async () =>
    {
        await call();
        return true;
    });

    private static string Clean(string system) => new(system.Where(char.IsLetterOrDigit).ToArray());
}
