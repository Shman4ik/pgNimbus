using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;
using PgNimbus.Core.Settings;

namespace PgNimbus.Core.QueryLab;

/// <summary>A save, rename or delete in Query Lab that was refused; the message says why, for the status line.</summary>
public sealed class SavedPlanStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Keeps Query Lab's saved plans: <c>&lt;appdata&gt;/pgNimbus/query-lab/index.json</c> lists
/// them, and each plan is a file of its own beside it, <c>&lt;id&gt;.plan</c>, read only when
/// it is opened. One file holding every plan would be rewritten whole on each save, and
/// a plan over a thousand partitions runs to megabytes.
/// </summary>
/// <remarks>
/// <para>
/// Saved plans are the user's, so nothing here drops one to make room: a save past
/// <see cref="MaxPlans"/> or <see cref="MaxPlanLength"/> is refused with a message. An
/// explicit save, so the plan is written as it is, like a saved query (a plan's filters
/// can quote literals).
/// </para>
/// <para>
/// Every change reads the index again under one process-wide lock, so two windows'
/// labs never drop each other's plans, and <see cref="Changed"/> tells every open lab.
/// An index that exists but can't be read refuses the change instead of writing a list
/// without the plans it couldn't read; one that can't be parsed is moved aside
/// (<see cref="AppDataFile.BackUpCorrupt"/>) and the store starts over. A plan file is
/// written before its index entry and removed after it, so the index never names a
/// plan that isn't there.
/// </para>
/// <para>
/// With no app data directory the plans live in memory for the session.
/// </para>
/// </remarks>
public sealed class SavedPlanStore
{
    public const int MaxPlans = 200;

    /// <summary>The longest plan kept, in characters: what <see cref="ExplainPlanTextParser"/> reads.</summary>
    public const int MaxPlanLength = ExplainPlanTextParser.MaxInputLength;

    private const string IndexFile = "index.json";
    private const string PlanExtension = ".plan";

    private static readonly Lock Gate = new();

    // The session's plans when there is no app data directory.
    private static readonly List<SavedPlan> MemoryIndex = [];
    private static readonly Dictionary<Guid, string> MemoryPlans = [];

    public SavedPlanStore(string? directory = null)
    {
        Directory = directory ?? AppDataPaths.Resolve("query-lab");
    }

    /// <summary>Raised after any store changes the plans, on the thread that changed them.</summary>
    public static event Action? Changed;

    /// <summary>Where the plans are kept; null when they live in memory for the session.</summary>
    public string? Directory { get; }

    private string? IndexPath => Directory is null ? null : Path.Combine(Directory, IndexFile);

    /// <summary>The saved plans, in the order they were saved; empty when the index can't be read.</summary>
    public IReadOnlyList<SavedPlan> Load()
    {
        lock (Gate)
        {
            return ReadIndex(throwIfUnreadable: false);
        }
    }

    /// <summary>The plan saved under <paramref name="id"/>, or null when there is none or it can't be read.</summary>
    public string? ReadPlan(Guid id)
    {
        if (Directory is null)
        {
            lock (Gate)
            {
                return MemoryPlans.GetValueOrDefault(id);
            }
        }

        try
        {
            var path = PlanPath(id);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves <paramref name="plan"/> with its text, replacing the saved plan with the same
    /// id if there is one. Throws <see cref="SavedPlanStoreException"/> when it is refused.
    /// </summary>
    public void Save(SavedPlan plan, string planText)
    {
        if (planText.Length > MaxPlanLength)
        {
            throw new SavedPlanStoreException(
                $"This plan is larger than {MaxPlanLength / (1024 * 1024)} MB of text, more than Query Lab keeps.");
        }

        lock (Gate)
        {
            var index = ReadIndex(throwIfUnreadable: true).ToList();
            var at = index.FindIndex(p => p.Id == plan.Id);
            if (at < 0 && index.Count >= MaxPlans)
            {
                throw new SavedPlanStoreException(
                    $"Query Lab already keeps {MaxPlans} plans, the most it holds. Delete some to save another.");
            }

            WritePlan(plan.Id, planText);
            if (at >= 0)
            {
                index[at] = plan;
            }
            else
            {
                index.Add(plan);
            }

            WriteIndex(index);
        }

        Changed?.Invoke();
    }

    /// <summary>Gives the saved plan <paramref name="id"/> a new name; nothing happens when there is none.</summary>
    public void Rename(Guid id, string name)
    {
        lock (Gate)
        {
            var index = ReadIndex(throwIfUnreadable: true).ToList();
            var at = index.FindIndex(p => p.Id == id);
            if (at < 0)
            {
                return;
            }

            index[at] = index[at] with { Name = name };
            WriteIndex(index);
        }

        Changed?.Invoke();
    }

    /// <summary>Deletes the saved plan <paramref name="id"/>, its entry first and then its file.</summary>
    public void Delete(Guid id)
    {
        lock (Gate)
        {
            var index = ReadIndex(throwIfUnreadable: true).ToList();
            if (index.RemoveAll(p => p.Id == id) == 0)
            {
                return;
            }

            WriteIndex(index);
            DeletePlan(id);
        }

        Changed?.Invoke();
    }

    private List<SavedPlan> ReadIndex(bool throwIfUnreadable)
    {
        if (IndexPath is not { } path)
        {
            return [.. MemoryIndex];
        }

        var index = AppDataFile.ReadJson(path, SavedPlanJsonContext.Default.ListSavedPlan);

        // ReadJson moves a file it can't parse aside, so one still there was not read at all.
        if (index is null && throwIfUnreadable && File.Exists(path))
        {
            throw new SavedPlanStoreException(
                $"Query Lab's list of saved plans ({path}) can't be read, so nothing was changed.");
        }

        return index ?? [];
    }

    private void WriteIndex(List<SavedPlan> index)
    {
        if (IndexPath is not { } path)
        {
            MemoryIndex.Clear();
            MemoryIndex.AddRange(index);
            return;
        }

        try
        {
            AppDataFile.EnsureDirectory(Directory);
            AppDataFile.WriteJson(path, index, SavedPlanJsonContext.Default.ListSavedPlan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SavedPlanStoreException($"Query Lab couldn't write its list of saved plans: {e.Message}", e);
        }
    }

    private void WritePlan(Guid id, string planText)
    {
        if (Directory is null)
        {
            MemoryPlans[id] = planText;
            return;
        }

        try
        {
            AppDataFile.EnsureDirectory(Directory);
            AppDataFile.WriteAllText(PlanPath(id), planText);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SavedPlanStoreException($"Query Lab couldn't save the plan: {e.Message}", e);
        }
    }

    private void DeletePlan(Guid id)
    {
        if (Directory is null)
        {
            MemoryPlans.Remove(id);
            return;
        }

        try
        {
            File.Delete(PlanPath(id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The entry is gone, so the plan is no longer listed; the file is only left behind.
        }
    }

    private string PlanPath(Guid id) => Path.Combine(Directory!, id.ToString("N") + PlanExtension);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<SavedPlan>))]
internal sealed partial class SavedPlanJsonContext : JsonSerializerContext;
