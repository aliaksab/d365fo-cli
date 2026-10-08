using D365FO.Core.Analysis;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// A model filter that matches nothing must fail, not answer zero.
/// </summary>
/// <remarks>
/// Every model filter ends in <c>m.Name = @model</c>. Passing the package folder where the model
/// was meant — <c>ApplicationSuite</c> for <c>Foundation</c> — used to scan nothing and return a
/// successful, empty result, indistinguishable from "this model has none". The sample model is
/// copied under a package folder of a different name to reproduce exactly that layout.
/// </remarks>
public class ModelFilterTests : IDisposable
{
    private static readonly string SampleModel =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Samples", "MiniAot", "TestModel"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-modelfilter-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;

    public ModelFilterTests()
    {
        CopyDirectory(SampleModel, Path.Combine(_root, "packages", "AppSuitePkg"));
        _dbPath = Path.Combine(_root, "index.sqlite");
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        foreach (var batch in new MetadataExtractor().ExtractAll(Path.Combine(_root, "packages")))
            _repo.ApplyExtract(batch, sourceFingerprint: null);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        GC.SuppressFinalize(this);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public void No_filter_resolves_to_no_filter()
    {
        Assert.True(ModelFilter.TryResolve(_repo, null, out var resolved, out var failure));
        Assert.Null(resolved);
        Assert.Null(failure);
    }

    [Fact]
    public void A_case_only_difference_resolves_to_the_indexed_spelling()
    {
        // SQLite's `=` is case-sensitive: "testmodel" would otherwise match nothing.
        Assert.True(ModelFilter.TryResolve(_repo, "testmodel", out var resolved, out _));
        Assert.Equal("TestModel", resolved);
    }

    [Fact]
    public void A_package_name_fails_and_names_the_models_in_it()
    {
        Assert.False(ModelFilter.TryResolve(_repo, "AppSuitePkg", out _, out var failure));

        Assert.Equal(D365FoErrorCodes.ModelNotFound, failure!.Error!.Code);
        Assert.Contains("is a package", failure.Error.Message);
        Assert.Contains("TestModel", failure.Error.Hint);
    }

    [Fact]
    public void A_typo_fails_with_the_nearest_models()
    {
        Assert.False(ModelFilter.TryResolve(_repo, "TestModl", out _, out var failure));

        Assert.Equal(D365FoErrorCodes.ModelNotFound, failure!.Error!.Code);
        Assert.Contains("Did you mean: TestModel", failure.Error.Hint);
    }

    [Fact]
    public void An_unrelated_name_fails_and_points_at_models_list()
    {
        Assert.False(ModelFilter.TryResolve(_repo, "Zzzzzzzzzz", out _, out var failure));

        Assert.Equal(D365FoErrorCodes.ModelNotFound, failure!.Error!.Code);
        Assert.Contains("d365fo models list", failure.Error.Hint);
    }

    [Theory]
    [InlineData(@"K:\AosService\PackagesLocalDirectory\ApplicationSuite\Foundation\AxClass\SalesLineType.xml")]
    [InlineData("/packages/ApplicationSuite/Foundation/AxClass/SalesLineType.xml")]
    public void The_package_is_read_off_either_separator(string path) =>
        Assert.Equal("ApplicationSuite", ModelFilter.PackageOf(path));

    [Fact]
    public void Find_references_fails_on_a_package_name_instead_of_answering_zero()
    {
        var result = new ToolHandlers(_repo).FindReferences("FmVehicle", kind: null, model: "AppSuitePkg");

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.ModelNotFound, result.Error!.Code);
    }

    [Fact]
    public void Find_references_honours_a_case_only_difference()
    {
        var exact = new ToolHandlers(_repo).FindReferences("FmVehicle", kind: null, model: "TestModel");
        var lower = new ToolHandlers(_repo).FindReferences("FmVehicle", kind: null, model: "testmodel");

        Assert.True(lower.Ok);
        Assert.Equal(CountOf(exact), CountOf(lower));
    }

    [Fact]
    public void Every_model_filtered_handler_rejects_an_unknown_model()
    {
        var h = new ToolHandlers(_repo);
        var results = new[]
        {
            h.FindTablesByField("VIN", "AppSuitePkg"),
            h.SearchClasses("Fm", "AppSuitePkg"),
            h.SearchTables("Fm", "AppSuitePkg"),
            h.AnalyzeFormPatterns(null, null, null, "AppSuitePkg", 10),
            h.AnalyzeIntegration("AppSuitePkg"),
            h.ReportIntegrations("AppSuitePkg"),
            h.FindBatchJobs("AppSuitePkg"),
            CodeAnalysis.Patterns(_repo, "number sequence", "AppSuitePkg"),
            CodeAnalysis.Implementations(_repo, "validateWrite", "AppSuitePkg"),
            CodeAnalysis.ApiUsage(_repo, "NumberSeq", "AppSuitePkg"),
        };

        Assert.All(results, r =>
        {
            Assert.False(r.Ok);
            Assert.Equal(D365FoErrorCodes.ModelNotFound, r.Error!.Code);
        });
    }

    private static int CountOf(ToolResult<object> r) =>
        (int)r.Data!.GetType().GetProperty("count")!.GetValue(r.Data)!;
}
