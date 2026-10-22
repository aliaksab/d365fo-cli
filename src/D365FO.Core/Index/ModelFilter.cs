// <copyright file="ModelFilter.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;

namespace D365FO.Core.Index;

/// <summary>
/// Resolves the <c>--model</c> / <c>model</c> filter of a read command against the index
/// before the query runs.
/// </summary>
/// <remarks>
/// Every model filter ends in <c>m.Name = @model</c>, which is case-sensitive and matches
/// nothing for a name that is not a model. A filter that matches nothing returns a clean,
/// successful zero — indistinguishable from "this model has none". The common way to get
/// there is passing the package folder (<c>ApplicationSuite</c>) where the model
/// (<c>Foundation</c>) was meant. So the name is resolved first: a case-only difference is
/// corrected to the indexed spelling, anything else fails <c>MODEL_NOT_FOUND</c> with the
/// models it could have meant.
/// </remarks>
public static class ModelFilter
{
    /// <summary>
    /// Resolve <paramref name="model"/> to its indexed spelling.
    /// </summary>
    /// <returns>
    /// True with <paramref name="resolved"/> set (null when no filter was given, or when the
    /// index has no models to check against); false with <paramref name="failure"/> set when
    /// the name is not an indexed model.
    /// </returns>
    public static bool TryResolve(
        MetadataRepository repo, string? model,
        out string? resolved, out ToolResult<object>? failure)
    {
        resolved = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(model)) return true;
        var name = model.Trim();

        var models = repo.ListModels().Select(m => m.Name).ToList();
        // An empty index answers every query with nothing anyway, and says why elsewhere.
        if (models.Count == 0)
        {
            resolved = name;
            return true;
        }

        resolved = models.FirstOrDefault(m => string.Equals(m, name, StringComparison.Ordinal))
                   ?? models.FirstOrDefault(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));
        if (resolved is not null) return true;

        failure = NotFound(repo, name, models);
        return false;
    }

    private static ToolResult<object> NotFound(MetadataRepository repo, string name, IReadOnlyList<string> models)
    {
        var inPackage = ModelsInPackage(repo, name);
        if (inPackage.Count > 0)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.ModelNotFound,
                $"'{name}' is a package, not a model — a model filter on it matches nothing.",
                $"Use a model in that package: {string.Join(", ", inPackage)}.");
        }

        var near = models
            .Select(m => (name: m, dist: NameSuggester.Distance(m, name)))
            .Where(t => t.dist <= Math.Max(2, name.Length / 3)
                        || t.name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.dist)
            .ThenBy(t => t.name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(t => t.name)
            .ToList();

        return ToolResult<object>.Fail(D365FoErrorCodes.ModelNotFound,
            $"Model '{name}' is not in the index — a model filter on it matches nothing.",
            near.Count > 0
                ? "Did you mean: " + string.Join(", ", near) + "? Run `d365fo models list` for every indexed model."
                : "Run `d365fo models list` for every indexed model.");
    }

    /// <summary>
    /// The indexed models whose files sit under a package folder called <paramref name="package"/>.
    /// </summary>
    internal static IReadOnlyList<string> ModelsInPackage(MetadataRepository repo, string package) =>
        repo.GetModelSamplePaths()
            .Where(r => string.Equals(PackageOf(r.SourcePath), package, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Model)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The package folder of an AOT source path: <c>&lt;Package&gt;/&lt;Model&gt;/Ax&lt;Type&gt;/&lt;Name&gt;.xml</c>.
    /// Split on both separators — an index built on Windows is read everywhere.
    /// </summary>
    internal static string? PackageOf(string sourcePath)
    {
        var parts = sourcePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4 ? parts[^4] : null;
    }
}
