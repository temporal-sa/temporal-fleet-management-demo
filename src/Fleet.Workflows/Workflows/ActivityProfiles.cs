using Fleet.Core;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Fleet.Workflows;

/// <summary>
/// Activity options in one place. Note what is NOT here: no dead-letter topics, no circuit breaker,
/// no retry loops in the integration code. Retries are a policy on the call.
/// </summary>
internal static class ActivityProfiles
{
    /// <summary>
    /// Salesforce and TMS calls. Retries forever with backoff capped at 10s, so a 20-minute TMS outage
    /// simply pauses the affected step and resumes in order when the system comes back.
    /// </summary>
    public static ActivityOptions Integration(DemoSettings settings) => new()
    {
        TaskQueue = settings.IntegrationsQueue,
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(10),
        },
    };

    /// <summary>Activities that run on the calling workflow's own task queue (telematics, vendors, IVR, Kafka).</summary>
    public static ActivityOptions Local() => new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(10),
        },
    };

    /// <summary>Cross-entity updates (reserve a truck, hold for maintenance). Business rejections are non-retryable.</summary>
    public static ActivityOptions Entity() => new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(20),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(5),
            MaximumAttempts = 10,
        },
    };

    /// <summary>LLM calls. Limited attempts; the workflow falls back to the policy engine.</summary>
    public static ActivityOptions Ai() => new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(45),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2,
            MaximumAttempts = 3,
        },
    };
}
