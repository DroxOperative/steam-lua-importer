using System;
using System.Collections.Generic;

namespace SteamLuaImporter
{
    /// <summary>
    /// Saved as config.json in the plugin's data folder.
    /// Edit it with Playnite closed to change anything not exposed in the menu.
    /// </summary>
    public class Settings
    {
        /// <summary>Folder scanned for {appId}.lua files.</summary>
        public string FolderPath { get; set; }

        /// <summary>Steam app types to import, as reported by the store API ("game", "dlc", ...).</summary>
        public List<string> AllowedTypes { get; set; } = new List<string> { "game", "demo", "mod", "advertising" };

        /// <summary>Tag added to every game this plugin imports.</summary>
        public string TagName { get; set; } = "";

        /// <summary>Pause between store API requests.</summary>
        public int RequestDelayMs { get; set; } = 1600;

        /// <summary>Days before an app ID that failed to resolve is looked up again.</summary>
        public int RetryFailedAfterDays { get; set; } = 7;
    }

    /// <summary>One cached store lookup, kept in lookup-cache.json so re-runs skip known IDs.</summary>
    public class AppLookup
    {
        public bool Success { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public DateTime CheckedUtc { get; set; }
    }
}
