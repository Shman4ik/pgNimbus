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
            [.. dto.Tables.Select(t => new CompletionTable(t.Schema, t.Name, [.. t.Columns.Select(c => ToColumn(t.Name, c))])
            {
                Kind = string.IsNullOrEmpty(t.Kind) ? 'r' : t.Kind[0],
                IsPartition = t.IsPartition,
                Comment = t.Comment,
                RowEstimate = t.RowEstimate,
            })],
            [.. dto.Functions.Select(ToFunction)],
            [.. dto.ForeignKeys.Select(k => new ForeignKeyInfo(k.FromSchema, k.FromTable, k.FromColumns, k.ToSchema, k.ToTable, k.ToColumns, k.ConstraintName))],
            dto.SearchPath)
        {
            BuiltinFunctions = [.. dto.Builtins.Select(ToFunction)],
            Types = [.. dto.Types.Select(t => new DataTypeInfo(t.Schema, t.Name, t.DisplayName, t.Kind[0]) { EnumLabels = t.EnumLabels ?? [] })],
            Sequences = [.. (dto.Sequences ?? []).Select(s => new SequenceName(s[0], s[1]))],
            Indexes = [.. (dto.Indexes ?? []).Select(s => new IndexName(s[0], s[1], s[2]))],
            Roles = dto.Roles ?? [],
            Settings = [.. (dto.Settings ?? []).Select(s => new SettingInfo(s.Name, s.VarType, s.ShortDescription, s.EnumValues ?? []))],
            Extensions = [.. (dto.Extensions ?? []).Select(e => new ExtensionInfo(e.Name, e.InstalledVersion, e.DefaultVersion, e.Description))],
        };
    }

    // A column is [name, type] or, with its facts, [name, type, flags, comment]:
    // flags are letters — N not null, D default, P primary key, G generated,
    // A/I identity always / by default.
    private static TableColumn ToColumn(string table, string[] c)
    {
        var flags = c.Length > 2 ? c[2] : "";
        return new TableColumn(table, c[0], c[1])
        {
            NotNull = flags.Contains('N'),
            HasDefault = flags.Contains('D'),
            IsPrimaryKey = flags.Contains('P'),
            IsGenerated = flags.Contains('G'),
            Identity = flags.Contains('A') ? 'a' : flags.Contains('I') ? 'd' : '\0',
            Comment = c.Length > 3 && c[3].Length > 0 ? c[3] : null,
        };
    }

    private static string[] FromColumn(TableColumn c)
    {
        var flags = string.Concat(
            c.NotNull ? "N" : "", c.HasDefault ? "D" : "", c.IsPrimaryKey ? "P" : "", c.IsGenerated ? "G" : "",
            c.Identity == 'a' ? "A" : c.Identity == 'd' ? "I" : "");
        return c.Comment is { } comment ? [c.Column, c.DataType, flags, comment]
            : flags.Length > 0 ? [c.Column, c.DataType, flags]
            : [c.Column, c.DataType];
    }

    public static void Save(CompletionCatalog catalog, string path)
    {
        var dto = new CatalogDto(
            [.. catalog.Schemas],
            [.. catalog.Tables.Select(t => new TableDto(t.Schema, t.Name, [.. t.Columns.Select(FromColumn)], t.Kind.ToString(), t.IsPartition, t.Comment, t.RowEstimate))],
            [.. catalog.Functions.Select(FromFunction)],
            [.. catalog.BuiltinFunctions.Select(FromFunction)],
            [.. catalog.ForeignKeys.Select(k => new ForeignKeyDto(k.FromSchema, k.FromTable, [.. k.FromColumns], k.ToSchema, k.ToTable, [.. k.ToColumns], k.ConstraintName))],
            [.. catalog.Types.Select(t => new TypeDto(t.Schema, t.Name, t.DisplayName, t.Kind.ToString(), t.EnumLabels.Count > 0 ? [.. t.EnumLabels] : null))],
            catalog.SearchPath?.ToList())
        {
            Sequences = [.. catalog.Sequences.Select(s => new[] { s.Schema, s.Name })],
            Indexes = [.. catalog.Indexes.Select(s => new[] { s.Schema, s.Name, s.Table })],
            Roles = [.. catalog.Roles],
            Settings = [.. catalog.Settings.Select(s => new SettingDto(s.Name, s.VarType, s.ShortDescription, s.EnumValues.Count > 0 ? [.. s.EnumValues] : null))],
            Extensions = [.. catalog.Extensions.Select(e => new ExtensionDto(e.Name, e.InstalledVersion, e.DefaultVersion, e.Description))],
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto));
    }

    /// <summary>The corpus's queries, line endings normalized to <c>\n</c>.</summary>
    public static List<string> LoadCorpus(string path) =>
        [.. File.ReadAllText(path).ReplaceLineEndings("\n").Split("\n---\n").Select(q => q.Trim('\n')).Where(q => q.Length > 0)];

    private static CompletionFunction ToFunction(FunctionDto f) =>
        new(f.Schema, new FunctionInfo(f.Name, f.Arguments, f.ReturnType, f.Kind[0])
        {
            IsInternal = f.IsInternal,
            FullArguments = f.FullArguments,
            Description = f.Description,
        });

    private static FunctionDto FromFunction(CompletionFunction f) =>
        new(f.Schema, f.Function.Name, f.Function.Arguments, f.Function.ReturnType, f.Function.Kind.ToString(), f.Function.IsInternal,
            f.Function.FullArguments == f.Function.Arguments ? null : f.Function.FullArguments, f.Function.Description);

    private sealed record CatalogDto(
        List<string> Schemas,
        List<TableDto> Tables,
        List<FunctionDto> Functions,
        List<FunctionDto> Builtins,
        List<ForeignKeyDto> ForeignKeys,
        List<TypeDto> Types,
        List<string>? SearchPath)
    {
        public List<string[]>? Sequences { get; init; }

        public List<string[]>? Indexes { get; init; }

        public List<string>? Roles { get; init; }

        public List<SettingDto>? Settings { get; init; }

        public List<ExtensionDto>? Extensions { get; init; }
    }

    private sealed record TableDto(
        string Schema, string Name, List<string[]> Columns, string? Kind = null, bool IsPartition = false,
        string? Comment = null, long? RowEstimate = null);

    private sealed record FunctionDto(
        string Schema, string Name, string Arguments, string ReturnType, string Kind, bool IsInternal = false,
        string? FullArguments = null, string? Description = null);

    private sealed record SettingDto(string Name, string VarType, string? ShortDescription, List<string>? EnumValues);

    private sealed record ExtensionDto(string Name, string? InstalledVersion, string DefaultVersion, string? Description);

    private sealed record ForeignKeyDto(
        string FromSchema, string FromTable, List<string> FromColumns,
        string ToSchema, string ToTable, List<string> ToColumns, string? ConstraintName);

    private sealed record TypeDto(string Schema, string Name, string DisplayName, string Kind, List<string>? EnumLabels = null);
}
