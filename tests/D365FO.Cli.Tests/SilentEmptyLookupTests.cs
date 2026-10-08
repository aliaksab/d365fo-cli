using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Cli.Commands.Find;
using D365FO.Cli.Commands.Get;
using D365FO.Cli.Commands.Index;
using D365FO.Core;

namespace D365FO.Cli.Tests;

/// <summary>
/// Lookups that used to answer a different question than the one asked, or nothing at all,
/// without saying so.
/// </summary>
/// <remarks>
/// Measured by an outside benchmark (CLI vs XRef DB vs grep): <c>find refs --xref</c> with the
/// bridge off quietly ran a 12-minute text scan and returned 42 of 72 callers;
/// <c>get table SalesLine</c> spent ~44k tokens, mostly method signatures, because
/// <c>--include</c> was declared and never read.
/// </remarks>
[Collection("EnvIndexDb")]
public sealed class SilentEmptyLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-silent-{Guid.NewGuid():N}");
    private readonly string? _oldIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
    private readonly string? _oldBridge = Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");

    public SilentEmptyLookupTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", Path.Combine(_root, "index.sqlite"));
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _oldIndexDb);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", _oldBridge);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static (int Exit, JsonElement Envelope) Run(Func<int> command)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(writer);
            exit = command();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return (exit, JsonDocument.Parse(writer.ToString()).RootElement.Clone());
    }

    private static string ErrorCode(JsonElement envelope) =>
        envelope.GetProperty("error").GetProperty("code").GetString()!;

    // ---- find refs --xref ----

    [Fact]
    public void Xref_with_the_bridge_off_fails_instead_of_falling_back_to_the_text_scan()
    {
        var (exit, envelope) = Run(() => new FindRefsCommand().Execute(null!,
            new FindRefsCommand.Settings { Name = "SalesLine", Xref = true, Output = "json" }));

        Assert.NotEqual(0, exit);
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(D365FoErrorCodes.XrefUnavailable, ErrorCode(envelope));
        Assert.Contains("D365FO_BRIDGE_ENABLED", envelope.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Xref_rejects_a_model_filter_it_cannot_apply()
    {
        var (exit, envelope) = Run(() => new FindRefsCommand().Execute(null!,
            new FindRefsCommand.Settings { Name = "SalesLine", Xref = true, Model = "Foundation", Output = "json" }));

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.BadInput, ErrorCode(envelope));
        Assert.Contains("--model", envelope.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Xref_rejects_an_artifact_kind_where_a_reference_kind_is_meant()
    {
        // `--kind class` means "artifact kind" to the text scan; the xref query would filter on a
        // reference kind called "class", which does not exist, and return an empty list.
        var (exit, envelope) = Run(() => new FindRefsCommand().Execute(null!,
            new FindRefsCommand.Settings { Name = "SalesLine", Xref = true, Kind = "class", Output = "json" }));

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.BadInput, ErrorCode(envelope));
        Assert.Contains("Call", envelope.GetProperty("error").GetProperty("hint").GetString());
    }

    [Fact]
    public void Xref_accepts_a_reference_kind_and_only_then_needs_the_bridge()
    {
        var (_, envelope) = Run(() => new FindRefsCommand().Execute(null!,
            new FindRefsCommand.Settings { Name = "SalesLine", Xref = true, Kind = "call", Output = "json" }));

        Assert.Equal(D365FoErrorCodes.XrefUnavailable, ErrorCode(envelope));
    }

    // ---- get table --include ----

    [Fact]
    public void Include_defaults_to_every_part_but_methods()
    {
        Assert.True(GetTableCommand.TryParseInclude(null, out var parts, out _));

        Assert.Contains("fields", parts);
        Assert.Contains("indexes", parts);
        Assert.Contains("relations", parts);
        Assert.Contains("deleteActions", parts);
        Assert.DoesNotContain("methods", parts);
    }

    [Theory]
    [InlineData("all", new[] { "fields", "indexes", "relations", "deleteActions", "methods" })]
    [InlineData("fields", new[] { "fields" })]
    [InlineData("Methods, delete-actions", new[] { "methods", "deleteActions" })]
    public void Include_selects_exactly_the_named_parts(string include, string[] expected)
    {
        Assert.True(GetTableCommand.TryParseInclude(include, out var parts, out _));
        Assert.Equal(expected.OrderBy(p => p), parts.Select(p => GetTableCommand.Parts.First(k =>
            string.Equals(k, p, StringComparison.OrdinalIgnoreCase))).OrderBy(p => p));
    }

    [Fact]
    public void Include_rejects_an_unknown_part_instead_of_ignoring_it()
    {
        Assert.False(GetTableCommand.TryParseInclude("fields,feilds", out _, out var failure));
        Assert.Equal(D365FoErrorCodes.BadInput, failure!.Error!.Code);
        Assert.Contains("feilds", failure.Error.Message);
    }

    [Fact]
    public void Include_trims_a_bridge_payload_and_counts_the_methods_it_dropped()
    {
        var payload = new JsonObject
        {
            ["Name"] = "SalesLine",
            ["Fields"] = new JsonArray("ItemId", "SalesPrice"),
            ["Methods"] = new JsonArray("find", "exist", "validateWrite"),
            ["Indexes"] = new JsonArray("TransIdIdx"),
        };
        GetTableCommand.TryParseInclude("fields", out var parts, out _);

        Assert.Equal(3, GetTableCommand.ApplyInclude(payload, parts));

        Assert.NotNull(payload["Fields"]);
        Assert.Null(payload["Methods"]);
        Assert.Null(payload["Indexes"]);
        Assert.Equal(3, (int)payload["methodCount"]!);
    }

    [Fact]
    public void Get_table_leaves_methods_out_by_default_and_says_how_many()
    {
        IndexOneTable();

        var (exit, envelope) = Run(() => new GetTableCommand().Execute(null!,
            new GetTableCommand.Settings { Name = "ConSilentTable", Output = "json" }));

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.True(data.TryGetProperty("fields", out _));
        Assert.False(data.TryGetProperty("methods", out _));
        Assert.Equal(2, data.GetProperty("methodCount").GetInt32());
        Assert.Contains("--include methods", envelope.GetProperty("warnings")[0].GetString());

        var (_, all) = Run(() => new GetTableCommand().Execute(null!,
            new GetTableCommand.Settings { Name = "ConSilentTable", Include = "methods", Output = "json" }));
        var allData = all.GetProperty("data");
        Assert.Equal(2, allData.GetProperty("methods").GetArrayLength());
        Assert.False(allData.TryGetProperty("fields", out _));
        Assert.False(allData.TryGetProperty("methodCount", out _));
    }

    private void IndexOneTable()
    {
        var packages = Path.Combine(_root, "packages");
        var tableDir = Path.Combine(packages, "ConPkg", "ConModel", "AxTable");
        Directory.CreateDirectory(tableDir);
        File.WriteAllText(Path.Combine(tableDir, "ConSilentTable.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ConSilentTable</Name>
              <SourceCode>
                <Declaration><![CDATA[public class ConSilentTable extends common {}]]></Declaration>
                <Methods>
                  <Method>
                    <Name>find</Name>
                    <Source><![CDATA[public static ConSilentTable find(str _id) { ConSilentTable t; return t; }]]></Source>
                  </Method>
                  <Method>
                    <Name>exist</Name>
                    <Source><![CDATA[public static boolean exist(str _id) { return true; }]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
              <Fields>
                <AxTableField xmlns="" i:type="AxTableFieldString">
                  <Name>ConId</Name>
                </AxTableField>
              </Fields>
            </AxTable>
            """);

        var code = IndexExtractCommand.ExtractCore(
            OutputMode.Kind.Json,
            packagesOverride: packages,
            databaseOverride: Path.Combine(_root, "index.sqlite"),
            onlyModel: null,
            sinceIso: null);
        Assert.Equal(0, code);
    }
}
