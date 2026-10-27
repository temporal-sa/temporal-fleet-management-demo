using Fleet.Core;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Fleet.Workflows;

/// <summary>
/// Search attribute upserts go through here so they can be switched off where the attributes are not
/// registered (the time-skipping test server). The switch is part of the workflow input, so it is deterministic.
/// </summary>
internal static class FleetVisibility
{
    public static void Upsert(DemoSettings settings, params SearchAttributeUpdate[] updates)
    {
        if (settings.UseSearchAttributes)
        {
            Workflow.UpsertTypedSearchAttributes(updates);
        }
    }
}
