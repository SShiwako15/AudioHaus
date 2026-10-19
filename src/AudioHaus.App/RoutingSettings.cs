using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AudioHaus.App;

/*
 * Name: RestorationRecord
 * Input: App identity, original render preferences, and the owned parking value.
 * Process: Records restoration intent before Windows preferences are changed.
 * Output: Recovery data independent of an already-exited or reused PID.
 */
public sealed record RestorationRecord(string ApplicationIdentity, string[] Preferences, string ParkingPreference);

/*
 * Name: RoutingSettings
 * Input: Optional per-user storage directory.
 * Process: Atomically persists cable configuration and outstanding restoration records.
 * Output: Durable recovery intent; malformed settings or failed writes throw visibly.
 */
internal sealed class RoutingSettings
{
    private readonly string path;
    public string? ParkingEndpointId { get; set; }
    public List<RestorationRecord> Restorations { get; } = new();

    /*
     * Name: RoutingSettings
     * Input: Storage directory, or null for the user's AudioHaus application data.
     * Process: Loads settings without silently discarding restoration information.
     * Output: Loaded state; unsupported/corrupt storage fails before policy changes.
     */
    public RoutingSettings(string? directory)
    {
        path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioHaus"), "routing.json");
        if (!File.Exists(path)) { return; }
        SavedSettings saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("AudioHaus settings are empty.");
        if (saved.Version != 1) { throw new InvalidDataException("Unsupported AudioHaus settings version."); }
        ParkingEndpointId = saved.ParkingEndpointId;
        foreach (RestorationRecord record in saved.Restorations)
        {
            if (string.IsNullOrWhiteSpace(record.ApplicationIdentity) || record.Preferences.Length != 3)
            { throw new InvalidDataException("AudioHaus restoration data is invalid."); }
            Restorations.Add(record);
        }
    }

    /*
     * Name: Save
     * Input: Current cable selection and restoration records.
     * Process: Replaces settings only after the complete temporary file is flushed.
     * Output: Durable intent, or an I/O exception before further Windows mutations.
     */
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedSettings(1, ParkingEndpointId, Restorations.ToArray()), new JsonSerializerOptions { WriteIndented = true });
        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, path, true);
    }

    /*
     * Name: SavedSettings
     * Input: Serialized version, cable ID, and restoration list.
     * Process: Defines the minimal persisted routing configuration.
     * Output: Versioned JSON data; route rules are not persisted.
     */
    private sealed record SavedSettings(int Version, string? ParkingEndpointId, RestorationRecord[] Restorations);
}
