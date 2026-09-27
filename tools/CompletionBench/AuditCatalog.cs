using System.Text.Json;
using PgNimbus.App.Completion;
using PgNimbus.Core.Schema;

namespace PgNimbus.CompletionBench;

/// <summary>
/// A completion catalog saved to JSON, so the audit's measurements run without
/// a server: <c>dump</c> writes one from a live database (through
/// <see cref="SqlCompletionProvider.ReadCatalogAsync"/>, the read the editor
/// itself does), everything else loads it. <c>Audit/catalog.json</c> is the
/// catalog of the audit stand (scripts/demo plus <c>Audit/saas.sql</c>).
/// </summary>
public static class AuditCatalog
{
    /// <summary>The stand's catalog, copied next to the binaries.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Audit", "catalog.json");

    /// <summary>The audit corpus: queries separated by a line holding only <c>---</c>.</summary>
    public static string DefaultCorpusPath => Path.Combine(AppContext.BaseDirectory, "Audit", "corpus.sql");

    public static CompletionCatalog Load(string path)
    {
        var dto = JsonSerializer.Deserialize<CatalogDto>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"{path} holds no catalog.");
        return new CompletionCatalog(
            dto.Schemas,
            [.. dto.Tables.Select(t => new CompletionTable(t.Schema, t.Name, [.. t.Columns.Select(c => new TableColumn(t.Name, c[0], c[1]))]))],
            [.. dto.Functions.Select(ToFunction)],
            [.. dto.ForeignKeys.Select(k => new ForeignKeyInfo(k.FromSchema, k.FromTable, k.FromColumns, k.ToSchema, k.ToTable, k.ToColumns, k.ConstraintName))],
            dto.SearchPath)
        {
            BuiltinFunctions = [.. dto.Builtins.Select(ToFunction)],
            Types = [.. dto.Types.Select(t => new DataTypeInfo(t.Schema, t.Name, t.DisplayName, t.Kind[0]))],
        };
    }

    public static void Save(CompletionCatalog catalog, string path)
    {
        var dto = new CatalogDto(
            [.. catalog.Schemas],
            [.. catalog.Tables.Select(t => new TableDto(t.Schema, t.Name, [.. t.Columns.Select(c => new[] { c.Column, c.DataType })]))],
            [.. catalog.Functions.Select(FromFunction)],
            [.. catalog.BuiltinFunctions.Select(FromFunction)],
            [.. catalog.ForeignKeys.Select(k => new ForeignKeyDto(k.FromSchema, k.FromTable, [.. k.FromColumns], k.ToSchema, k.ToTable, [.. k.ToColumns], k.ConstraintName))],
            [.. catalog.Types.Select(t => new TypeDto(t.Schema, t.Name, t.DisplayName, t.Kind.ToString()))],
            catalog.SearchPath?.ToList());
        File.WriteAllText(path, JsonSerializer.Serialize(dto));
    }

    /// <summary>The corpus's queries, line endings normalized to <c>\n</c>.</summary>
    public static List<string> LoadCorpus(string path) =>
        [.. File.ReadAllText(path).ReplaceLineEndings("\n").Split("\n---\n").Select(q => q.Trim('\n')).Where(q => q.Length > 0)];

    private static CompletionFunction ToFunction(FunctionDto f) =>
        new(f.Schema, new FunctionInfo(f.Name, f.Arguments, f.ReturnType, f.Kind[0]));

    private static FunctionDto FromFunction(CompletionFunction f) =>
        new(f.Schema, f.Function.Name, f.Function.Arguments, f.Function.ReturnType, f.Function.Kind.ToString());

    private sealed record CatalogDto(
        List<string> Schemas,
        List<TableDto> Tables,
        List<FunctionDto> Functions,
        List<FunctionDto> Builtins,
        List<ForeignKeyDto> ForeignKeys,
        List<TypeDto> Types,
        List<string>? SearchPath);

    private sealed record TableDto(string Schema, string Name, List<string[]> Columns);

    private sealed record FunctionDto(string Schema, string Name, string Arguments, string ReturnType, string Kind);

    private sealed record ForeignKeyDto(
        string FromSchema, string FromTable, List<string> FromColumns,
        string ToSchema, string ToTable, List<string> ToColumns, string? ConstraintName);

    private sealed record TypeDto(string Schema, string Name, string DisplayName, string Kind);
}
