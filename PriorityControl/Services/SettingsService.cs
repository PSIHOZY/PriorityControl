using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using PriorityControl.Models;

namespace PriorityControl.Services
{
    internal sealed class SettingsService
    {
        private readonly string _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PriorityControl");

        private const string FileName = "settings.json";

        public AppSettings Load()
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    var settings = serializer.ReadObject(stream) as AppSettings ?? new AppSettings();
                    Normalize(settings);
                    return settings;
                }
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            Normalize(settings);
            Directory.CreateDirectory(_settingsDirectory);

            using (var stream = File.Create(GetSettingsPath()))
            {
                var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                serializer.WriteObject(stream, settings);
            }
        }

        private string GetSettingsPath()
        {
            return Path.Combine(_settingsDirectory, FileName);
        }

        private static void Normalize(AppSettings settings)
        {
            if (settings.Entries == null)
            {
                settings.Entries = new List<AppEntry>();
                return;
            }

            foreach (AppEntry entry in settings.Entries)
            {
                if (entry == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    entry.Id = Guid.NewGuid().ToString("N");
                }

                entry.ExePath = entry.ExePath ?? string.Empty;
                entry.RuntimeStatus = "Not running";
                entry.ProcessId = null;
                entry.IsPriorityLocked = false;
            }
        }
    }
}
