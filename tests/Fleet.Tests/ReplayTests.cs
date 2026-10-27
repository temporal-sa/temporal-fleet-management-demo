using Fleet.Workflows;
using Temporalio.Common;
using Temporalio.Worker;
using Xunit;

namespace Fleet.Tests;

/// <summary>
/// The deploy guardrail. Export real histories (scripts/export-history.*) into tests/Fleet.Tests/histories,
/// then every build replays them against the current workflow code. If a change (AI-written or not) would
/// break running workflows, this test fails before the deploy does.
/// </summary>
public class ReplayTests
{
    [Fact]
    public async Task RecordedHistoriesReplayAgainstCurrentCode()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "histories");
        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : Array.Empty<string>();
        if (files.Length == 0)
        {
            return; // Nothing exported yet.
        }

        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions()
            .AddWorkflow<TruckWorkflow>()
            .AddWorkflow<DriverWorkflow>()
            .AddWorkflow<LoadWorkflow>()
            .AddWorkflow<MaintenanceWorkflow>()
            .AddWorkflow<SafetyCoachingWorkflow>()
            .AddWorkflow<EquipmentSyncWorkflow>());

        foreach (var file in files)
        {
            var workflowId = Path.GetFileNameWithoutExtension(file);
            await replayer.ReplayWorkflowAsync(WorkflowHistory.FromJson(workflowId, await File.ReadAllTextAsync(file)));
        }
    }
}
