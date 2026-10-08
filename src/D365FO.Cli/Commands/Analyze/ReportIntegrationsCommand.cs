using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Analyze;

/// <summary>
/// Aggregated integration surface report: OData entities, custom services,
/// business events, workflow types, and batch jobs for a model or workspace.
/// </summary>
public sealed class ReportIntegrationsCommand : Command<ReportIntegrationsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Restrict to a single model.")]
        public string? Model { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind   = OutputMode.Resolve(settings.Output);
        var repo   = RepoFactory.Create();
        if (!ModelFilter.TryResolve(repo, settings.Model, out var model, out var badModel))
            return RenderHelpers.Render(kind, badModel!);
        var report = repo.GetIntegrationReport(model);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            odataEntities = new
            {
                count = report.ODataEntities.Count,
                items = report.ODataEntities.Select(e => new
                {
                    e.Name,
                    e.Model,
                    e.PublicEntityName,
                    e.PublicCollectionName,
                    hasStagingTable = e.StagingTable is not null,
                }),
            },
            customServices = new
            {
                count = report.CustomServices.Count,
                items = report.CustomServices.Select(s => new { s.Name, s.Model, s.Class }),
            },
            businessEvents = new
            {
                count = report.BusinessEvents.Count,
                byCategory = report.BusinessEvents
                    .GroupBy(e => e.Category ?? "Uncategorized")
                    .OrderBy(g => g.Key)
                    .Select(g => new { category = g.Key, count = g.Count(), items = g.Select(e => e.Name) }),
            },
            workflowTypes = new
            {
                count = report.WorkflowTypes.Count,
                items = report.WorkflowTypes.Select(w => new { w.Name, w.Model, w.Category }),
            },
            batchJobs = new
            {
                count = report.BatchJobs.Count,
                items = report.BatchJobs.Select(c => new { c.Name, c.Model }),
            },
        }));
    }
}
