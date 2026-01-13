using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UOMapWeaver.Core;

namespace UOMapWeaver.App;

public static class AppSettings
{
    private static readonly object SyncRoot = new();
    private static AppUiState _state = new();

    public static bool SaveEnabled => _state.SaveFields;

    public static void Load()
    {
        lock (SyncRoot)
        {
            try
            {
                if (!File.Exists(UOMapWeaverDataPaths.UiStatePath))
                {
                    _state = new AppUiState { SaveFields = true };
                    return;
                }

                var json = File.ReadAllText(UOMapWeaverDataPaths.UiStatePath);
                var loaded = JsonSerializer.Deserialize<AppUiState>(json);
                _state = loaded ?? new AppUiState { SaveFields = true };
            }
            catch
            {
                _state = new AppUiState { SaveFields = true };
            }
        }
    }

    public static void Save()
    {
        lock (SyncRoot)
        {
            try
            {
                Directory.CreateDirectory(UOMapWeaverDataPaths.DataRoot);
                var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(UOMapWeaverDataPaths.UiStatePath, json);
            }
            catch
            {
                // Ignore persistence errors.
            }
        }
    }

    public static void SetSaveEnabled(bool enabled)
    {
        _state.SaveFields = enabled;
        Save();
    }

    public static void Reset()
    {
        lock (SyncRoot)
        {
            _state = new AppUiState();
            try
            {
                if (File.Exists(UOMapWeaverDataPaths.UiStatePath))
                {
                    File.Delete(UOMapWeaverDataPaths.UiStatePath);
                }
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    public static string GetString(string key, string defaultValue = "")
        => _state.Fields.TryGetValue(key, out var value) ? value : defaultValue;

    public static bool GetBool(string key, bool defaultValue = false)
        => _state.Flags.TryGetValue(key, out var value) ? value : defaultValue;

    public static int GetInt(string key, int defaultValue = 0)
        => _state.Numbers.TryGetValue(key, out var value) ? value : defaultValue;

    public static string[] GetList(string key)
        => _state.Lists.TryGetValue(key, out var value) ? value : Array.Empty<string>();

    public static void SetString(string key, string value)
    {
        _state.Fields[key] = value;
        SaveIfEnabled();
    }

    public static void SetBool(string key, bool value)
    {
        _state.Flags[key] = value;
        SaveIfEnabled();
    }

    public static void SetInt(string key, int value)
    {
        _state.Numbers[key] = value;
        SaveIfEnabled();
    }

    public static void SetList(string key, IEnumerable<string> values)
    {
        _state.Lists[key] = values.ToArray();
        SaveIfEnabled();
    }

    private static void SaveIfEnabled()
    {
        if (SaveEnabled)
        {
            Save();
        }
    }

    private sealed class AppUiState
    {
        public bool SaveFields { get; set; } = true;

        public Dictionary<string, string> Fields { get; set; } = new();

        public Dictionary<string, bool> Flags { get; set; } = new();

        public Dictionary<string, int> Numbers { get; set; } = new();

        public Dictionary<string, string[]> Lists { get; set; } = new();
    }
}
