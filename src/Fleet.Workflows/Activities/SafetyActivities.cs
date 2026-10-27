using Fleet.Core;
using Fleet.Integrations;
using Temporalio.Activities;

namespace Fleet.Workflows.Activities;

public class SafetyActivities
{
    private readonly IvrClient ivr;

    public SafetyActivities(IvrClient ivr) => this.ivr = ivr;

    [Activity]
    public Task PlaceCoachingCallAsync(CoachingCall call)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return ExternalCall.RunAsync(() => ivr.PlaceCallAsync(call, ct));
    }
}
