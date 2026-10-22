using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Analyze;

/// <summary>
/// Cross-checks indexed data entities for integration readiness.
/// Checks: duplicate PublicEntityName, missing PublicCollectionName, no staging table,
/// zero mapped fields, no mandatory fields.
/// </summary>
public sealed class AnalyzeIntegrationCommand : Command<AnalyzeIntegrationCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Restrict to a single model.")]
        public string? Model { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        if (!ModelFilter.TryResolve(repo, settings.Model, out var model, out var badModel))
            return RenderHelpers.Render(kind, badModel!);
        var issues = repo.AnalyzeIntegration(model);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count  = issues.Count,
            issues,
        }));
    }
}
