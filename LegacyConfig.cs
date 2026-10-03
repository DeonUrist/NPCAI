using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace NPCAI
{
    // First-run migration reads the legacy file only. Existing per-mod settings always win.
    internal static class LegacyConfig
    {
        internal static void Import(ConfigFile config, ManualLogSource log, bool existingConfig)
        {
            if (existingConfig) return;
            var legacy = Path.Combine(Paths.ConfigPath, "com.denis.apocalypter.apocaraider.cfg");
            if (!File.Exists(legacy)) return;
            try
            {
                var values = new Dictionary<ConfigDefinition, string>();
                string section = null;
                foreach (var raw in File.ReadAllLines(legacy))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                    var equals = line.IndexOf('=');
                    if (section == null || equals <= 0) continue;
                    values[new ConfigDefinition(section, line.Substring(0, equals).Trim())] = line.Substring(equals + 1).Trim();
                }
                int imported = 0;
                var save = config.SaveOnConfigSet;
                config.SaveOnConfigSet = false;
                try
                {
                    foreach (var key in config.Keys)
                    {
                        // New plugin discovery should remain opted in even if the combined mod was hidden.
                        if (key.Key == "Apocasetter") continue;
                        string value;
                        if (!values.TryGetValue(key, out value)) continue;
                        try { config[key].SetSerializedValue(value); imported++; }
                        catch (Exception e) { log.LogWarning("Legacy setting " + key + ": " + e.Message); }
                    }
                }
                finally { config.SaveOnConfigSet = save; }
                config.Save();
                log.LogInfo("Imported " + imported + " settings from Apocaraider; legacy file unchanged.");
            }
            catch (Exception e) { log.LogWarning("Legacy configuration import: " + e.Message); }
        }
    }
}
