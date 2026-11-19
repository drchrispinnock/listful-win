using System.Text.Json;
using System.Text.Json.Nodes;

namespace Listful;

/// <summary>
/// The Mac's <c>UserDefaults</c>: a JSON file of preferences, by default
/// <c>%LOCALAPPDATA%\Listful\preferences.json</c>. Keys are upstream's, so the two apps' settings
/// read the same. Reads and writes happen on the UI thread; each write saves the file.
/// </summary>
public sealed class Preferences
{
    // Upstream's keys (LibraryModel.Keys, DisplaySettings, ViewOptions).
    public const string FolderKey = "libraryFolder";
    public const string SelectedListKey = "selectedList";
    public const string LinkedFilesKey = "linkedFiles";
    public const string LinkedFoldersKey = "linkedFolders";
    public const string ShowDecruftedKey = "showDecrufted";
    public const string ShowTickKey = "showCompletionTick";
    public const string ShowStrikethroughKey = "showCompletionStrikethrough";
    public const string DeleteDecruftedKey = "deleteDecruftedTasks";
    public const string WindowBoundsKey = "windowBounds";
    public const string SidebarWidthKey = "sidebarWidth";

    private readonly string? path;
    private readonly JsonObject values;

    /// <summary>Raised after any value changes, with its key.</summary>
    public event Action<string>? Changed;

    public Preferences(string? path)
    {
        this.path = path;
        values = Load(path);
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Listful", "preferences.json");

    /// <summary>Preferences kept in memory only, for tests and previews.</summary>
    public static Preferences Temporary() => new(null);

    private static JsonObject Load(string? path)
    {
        if (path == null || !File.Exists(path)) return [];
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public string? GetString(string key) => values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public bool GetBool(string key, bool fallback = false) =>
        values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public double? GetDouble(string key) => values[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    public List<string> GetStrings(string key) =>
        values[key] is JsonArray array ? array.Select(n => n?.GetValue<string>()).OfType<string>().ToList() : [];

    public void Set(string key, string? value) => Store(key, value == null ? null : JsonValue.Create(value));
    public void Set(string key, bool value) => Store(key, JsonValue.Create(value));
    public void Set(string key, double value) => Store(key, JsonValue.Create(value));
    public void Set(string key, IEnumerable<string> value) => Store(key, new JsonArray(value.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));

    private void Store(string key, JsonNode? node)
    {
        if (JsonNode.DeepEquals(values[key], node)) return;
        if (node == null) values.Remove(key); else values[key] = node;
        Save();
        Changed?.Invoke(key);
    }

    private void Save()
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // MARK: - Display settings (upstream's DisplaySettings and ViewOptions)

    /// <summary>Done tasks show a tick. At least one of this and <see cref="ShowStrikethrough"/> stays on.</summary>
    public bool ShowTick { get => GetBool(ShowTickKey, true); set => Set(ShowTickKey, value); }
    public bool ShowStrikethrough { get => GetBool(ShowStrikethroughKey, true); set => Set(ShowStrikethroughKey, value); }
    /// <summary>Decruft deletes done tasks instead of hiding them.</summary>
    public bool DeleteDecrufted { get => GetBool(DeleteDecruftedKey); set => Set(DeleteDecruftedKey, value); }
    public bool ShowDecrufted { get => GetBool(ShowDecruftedKey); set => Set(ShowDecruftedKey, value); }
}
