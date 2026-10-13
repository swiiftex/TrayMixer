using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayMixer
{
    sealed class Settings
    {
        static readonly string FolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayMixer");
        static readonly string FilePath = Path.Combine(FolderPath, "settings.ini");
        const string VolumePrefix = "app.";

        // Remembered levels by app key (lower-case exe name, or one of the AudioEngine special keys).
        public Dictionary<string, Level> Volumes = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);
        public bool IsFirstRun;

        public static Settings Load()
        {
            var settings = new Settings();
            if (!File.Exists(FilePath))
            {
                settings.IsFirstRun = true;
                return settings;
            }
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0 || !line.StartsWith(VolumePrefix, StringComparison.Ordinal))
                        continue;
                    string key = line.Substring(VolumePrefix.Length, eq - VolumePrefix.Length).Trim();
                    string[] parts = line.Substring(eq + 1).Split(',');
                    if (key.Length > 0 && int.TryParse(parts[0].Trim(), out int volume))
                        settings.Volumes[key] = new Level(volume, parts.Length > 1 && parts[1].Trim() == "muted");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return settings;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            foreach (var entry in Volumes)
                sb.Append(VolumePrefix).Append(entry.Key).Append('=').Append(entry.Value.Volume)
                  .Append(entry.Value.Muted ? ",muted" : "").Append("\r\n");
            try
            {
                Directory.CreateDirectory(FolderPath);
                File.WriteAllText(FilePath, sb.ToString());
                IsFirstRun = false;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static class Startup
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "TrayMixer";

        static string Command => "\"" + Application.ExecutablePath + "\" --tray";

        public static bool IsEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return key?.GetValue(ValueName) is string;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enabled)
                    key.SetValue(ValueName, Command);
                else
                    key.DeleteValue(ValueName, false);
            }
        }

        // If the app was moved or reinstalled, point the existing startup entry at this copy.
        public static void RepairPath()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, Command);
            }
        }
    }
}
