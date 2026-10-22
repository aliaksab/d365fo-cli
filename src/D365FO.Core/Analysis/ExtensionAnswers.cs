using D365FO.Core.Index;

namespace D365FO.Core.Analysis;

/// <summary>
/// "What extends this object, and what does the object look like once they are all folded in?"
/// </summary>
/// <remarks>
/// The merged view was reachable over MCP (<c>extension_info(mode=table-merge)</c>) and from
/// nowhere on the CLI, which is the wrong way round for a repository whose CLI is the primary
/// surface. It lives here now so <c>d365fo find extensions --merged</c> and the MCP tool return
/// the same answer rather than two that agree by inspection.
/// </remarks>
public static class ExtensionAnswers
{
    /// <summary>The members the merged view folds in, keyed by their JSON name.</summary>
    public static readonly IReadOnlyList<string> MergedParts = ["fields", "indexes", "relations", "fieldGroups"];

    /// <param name="repo">Index to read.</param>
    /// <param name="table">Base table, or the name of one of its extensions.</param>
    /// <param name="parts">
    /// Which of <see cref="MergedParts"/> to return (case-insensitive); null returns them all.
    /// An unselected part is null and so left out of the JSON.
    /// </param>
    public static ToolResult<object> TableMerge(MetadataRepository repo, string table, IReadOnlySet<string>? parts = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(table))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Target required.");

        var items = repo.FindExtensions(table, "Table");
        var merged = TableMergeAnalyzer.Merge(repo, table);

        var warnings = merged.Unreadable.Count > 0
            ? new List<string>
            {
                $"{merged.Unreadable.Count} extension(s) could not be read, so the merged schema is INCOMPLETE: "
                + string.Join("; ", merged.Unreadable),
            }
            : null;

        return ToolResult<object>.Success(new
        {
            target = merged.Table,
            baseModel = merged.BaseModel,
            count = items.Count,
            extensions = items,
            merged = new
            {
                fields = Selected("fields") ? merged.Fields : null,
                indexes = Selected("indexes") ? merged.Indexes : null,
                relations = Selected("relations") ? merged.Relations : null,
                fieldGroups = Selected("fieldGroups") ? merged.FieldGroups : null,
                complete = merged.Unreadable.Count == 0,
            },
        }, warnings);

        bool Selected(string part) =>
            parts is null || parts.Any(p => string.Equals(p, part, StringComparison.OrdinalIgnoreCase));
    }
}
