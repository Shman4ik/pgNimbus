namespace PgNimbus.Core.Connections;

/// <summary>
/// Which environment a profile points at (ROADMAP T3). Persisted in
/// connections.json as its number, so members are only ever appended. The zero
/// value is <see cref="None"/>, which is what a profile saved before this field
/// existed loads as.
/// </summary>
public enum ConnectionEnvironment
{
    None,
    Development,
    Staging,
    Production,
}

public static class ConnectionEnvironments
{
    /// <summary>The picker's order: none first, production last.</summary>
    public static IReadOnlyList<ConnectionEnvironment> All { get; } =
        [ConnectionEnvironment.None, ConnectionEnvironment.Development, ConnectionEnvironment.Staging, ConnectionEnvironment.Production];

    /// <summary>The picker's label for <paramref name="environment"/>.</summary>
    public static string Label(this ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Development => "Development",
        ConnectionEnvironment.Staging => "Staging",
        ConnectionEnvironment.Production => "Production",
        _ => "None",
    };

    /// <summary>
    /// The short word the window and the connection list show, or null for
    /// <see cref="ConnectionEnvironment.None"/>, which shows nothing.
    /// </summary>
    public static string? Tag(this ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Development => "dev",
        ConnectionEnvironment.Staging => "staging",
        ConnectionEnvironment.Production => "production",
        _ => null,
    };

    /// <summary>
    /// The one line the picker shows under the choice: what pgNimbus does with
    /// it. Only production changes behaviour.
    /// </summary>
    public static string Description(this ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Production =>
            "Writes are off when a window opens. Allow them for that window when you mean to change data.",
        ConnectionEnvironment.None => "No label.",
        _ => "A label in the window. Writes are allowed.",
    };
}
