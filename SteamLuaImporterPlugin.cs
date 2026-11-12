using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace SteamLuaImporter
{
    public class SteamLuaImporterPlugin : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Id of Playnite's built-in Steam library plugin.</summary>
        public static readonly Guid SteamPluginId = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        private const string MenuSection = "@Steam Lua Importer";
        private const string CacheFileName = "lookup-cache.json";

        public override Guid Id { get; } = Guid.Parse("b7f1e0a9-c094-44e8-8b08-d3b46f08d903");

        private Settings settings;

        public SteamLuaImporterPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = false };
            settings = LoadPluginSettings<Settings>() ?? new Settings();
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Import Steam games from lua folder",
                Action = _ => SafeRun(ImportFromFolder)
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Change folder...",
                Action = _ => SafeRun(() => ChooseFolder())
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Clear Steam lookup cache",
                Action = _ => SafeRun(ClearCache)
            };
        }

        private void SafeRun(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                logger.Error(e, "Steam Lua Importer failed.");
                PlayniteApi.Dialogs.ShowErrorMessage(e.Message, "Steam Lua Importer");
            }
        }

        private bool ChooseFolder()
        {
            var picked = PlayniteApi.Dialogs.SelectFolder();
            if (string.IsNullOrEmpty(picked))
            {
                return false;
            }

            settings.FolderPath = picked;
            SavePluginSettings(settings);
            return true;
        }

        private void ImportFromFolder()
        {
            if (!Directory.Exists(settings.FolderPath) && !ChooseFolder())
            {
                return;
            }

            var steamPlugin = PlayniteApi.Addons.Plugins.OfType<LibraryPlugin>().FirstOrDefault(p => p.Id == SteamPluginId);
            if (steamPlugin == null)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Playnite's Steam library integration isn't installed or enabled.", "Steam Lua Importer");
                return;
            }

            // 1-2. Scan the folder for {AppID}.txt.
            var folderIds = ScanFolder(settings.FolderPath);
            if (folderIds.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage($"No {{AppID}}.lua files found in:\n{settings.FolderPath}", "Steam Lua Importer");
                return;
            }

            // 3a. Skip AppIDs already in Playnite as Steam games.
            var existing = new HashSet<string>(
                PlayniteApi.Database.Games.Where(g => g.PluginId == SteamPluginId).Select(g => g.GameId),
                StringComparer.OrdinalIgnoreCase);
            // 3a'. Also respect the user's import exclusion list for the Steam library.
            var excluded = new HashSet<string>(
                PlayniteApi.Database.ImportExclusions.Where(x => x.LibraryId == SteamPluginId).Select(x => x.GameId),
                StringComparer.OrdinalIgnoreCase);

            var candidates = folderIds.Where(id => !existing.Contains(id.ToString()) && !excluded.Contains(id.ToString())).ToList();
            var excludedCount = folderIds.Count(id => !existing.Contains(id.ToString()) && excluded.Contains(id.ToString()));
            // 3a''. Enumerate exclusion list skips in Playnite's `extensions.log`.
            foreach (var x in PlayniteApi.Database.ImportExclusions
                         .Where(x => x.LibraryId == SteamPluginId)
                         .Where(x => folderIds.Any(id => id.ToString() == x.GameId) && !existing.Contains(x.GameId))
                         .OrderBy(x => x.Name))
            {
                logger.Info($"Skipped (on import exclusion list): {x.GameId} {x.Name}");
            }
            if (candidates.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    $"Found {folderIds.Count} AppIDs; all are already in Playnite or on your import exclusion list.", "Steam Lua Importer");
                return;
            }

            // 3b. Resolve names and types (cached; network calls are paced for Steam's rate limit).
            var cache = LoadCache();
            var results = new Dictionary<uint, AppLookup>();
            var client = new SteamStoreClient();
            var allowedTypes = new HashSet<string>(
                (settings.AllowedTypes ?? new List<string>()).Select(t => t.Trim().ToLowerInvariant()));

            var progress = PlayniteApi.Dialogs.ActivateGlobalProgress(a =>
            {
                a.IsIndeterminate = false;
                a.ProgressMaxValue = candidates.Count;
                var networkCalls = 0;

                for (int i = 0; i < candidates.Count; i++)
                {
                    if (a.CancelToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var id = candidates[i];
                    a.CurrentProgressValue = i;
                    a.Text = $"Looking up Steam AppID {id} ({i + 1} of {candidates.Count})...";

                    if (cache.TryGetValue(id.ToString(), out var cached) && IsCacheUsable(cached))
                    {
                        results[id] = cached;
                        continue;
                    }

                    if (networkCalls > 0 && a.CancelToken.WaitHandle.WaitOne(Math.Max(0, settings.RequestDelayMs)))
                    {
                        break;
                    }

                    networkCalls++;
                    var lookup = client.Lookup(id, a.CancelToken);
                    if (lookup != null)
                    {
                        results[id] = lookup;
                        cache[id.ToString()] = lookup;
                        if (networkCalls % 25 == 0)
                        {
                            SaveCache(cache);
                        }
                    }
                }

                SaveCache(cache);
            }, new GlobalProgressOptions("Steam Lua Importer", true));

            if (progress.Error != null)
            {
                throw progress.Error;
            }

            var toImport = results.Where(r => r.Value.Success && allowedTypes.Contains(r.Value.Type ?? ""))
                                  .Select(r => new KeyValuePair<uint, string>(r.Key, CleanName(r.Value.Name)))
                                  .ToList();
            var skippedByType = results.Where(r => r.Value.Success && !allowedTypes.Contains(r.Value.Type ?? ""))
                                       .GroupBy(r => string.IsNullOrEmpty(r.Value.Type) ? "unknown" : r.Value.Type)
                                       .ToDictionary(g => g.Key, g => g.Count());
            var unresolved = results.Where(r => !r.Value.Success).Select(r => r.Key).ToList();
            var notChecked = candidates.Count - results.Count;

            if (unresolved.Count > 0)
            {
                var answer = PlayniteApi.Dialogs.ShowMessage(
                    $"{unresolved.Count} AppIDs couldn't be found on the Steam store (removed, unlisted or region-locked).\n\n" +
                    "Import them anyway with placeholder names (\"Steam app <id>\")? " +
                    "Download Metadata may fill in the real name if your settings take Name from Steam.",
                    "Steam Lua Importer", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer == MessageBoxResult.Yes)
                {
                    toImport.AddRange(unresolved.Select(id => new KeyValuePair<uint, string>(id, $"Steam app {id}")));
                }
            }

            // 3c-4. Import as uninstalled Steam-plugin games with Source = Steam.
            var imported = new List<Guid>();
            using (PlayniteApi.Database.BufferedUpdate())
            {
                foreach (var item in toImport)
                {
                    var metadata = new GameMetadata
                    {
                        GameId = item.Key.ToString(),
                        Name = item.Value,
                        IsInstalled = false,
                        Source = new MetadataNameProperty("Steam"),
                        Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") },
                    };

                    if (!string.IsNullOrWhiteSpace(settings.TagName))
                    {
                        metadata.Tags = new HashSet<MetadataProperty> { new MetadataNameProperty(settings.TagName.Trim()) };
                    }

                    var game = PlayniteApi.Database.ImportGame(metadata, steamPlugin);
                    imported.Add(game.Id);
                }
            }

            // 5. Select the new games so Download Metadata can run on them with the user's saved settings.
            if (imported.Count > 0)
            {
                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    PlayniteApi.MainView.SwitchToLibraryView();
                    PlayniteApi.MainView.SelectGames(imported);
                });
            }

            PlayniteApi.Dialogs.ShowMessage(BuildSummary(folderIds.Count, folderIds.Count - candidates.Count - excludedCount,
                excludedCount, imported.Count, skippedByType, unresolved.Count, notChecked, progress.Canceled), "Steam Lua Importer");
        }

        private string BuildSummary(int found, int alreadyPresent, int excluded, int imported, Dictionary<string, int> skippedByType,
            int unresolved, int notChecked, bool canceled)
        {
            var sb = new StringBuilder();
            if (canceled)
            {
                sb.AppendLine("Cancelled. Games looked up before cancelling were still imported.\n");
            }

            sb.AppendLine($"AppIDs in folder: {found}");
            sb.AppendLine($"Already in Playnite: {alreadyPresent}");
            if (excluded > 0)
            {
                sb.AppendLine($"On your import exclusion list: {excluded}");
            }

            sb.AppendLine($"Imported: {imported}");
            foreach (var kv in skippedByType.OrderByDescending(kv => kv.Value))
            {
                sb.AppendLine($"Skipped ({kv.Key}): {kv.Value}");
            }

            if (unresolved > 0)
            {
                sb.AppendLine($"Not found on Steam store: {unresolved}");
            }

            if (notChecked > 0)
            {
                sb.AppendLine($"Not looked up yet (cancelled or Steam unreachable): {notChecked}. Run again to continue.");
            }

            if (imported > 0)
            {
                sb.AppendLine();
                sb.AppendLine(string.IsNullOrWhiteSpace(settings.TagName)
                    ? "The new games are selected."
                    : "The new games are selected and tagged \"" + settings.TagName + "\".");
                sb.AppendLine("Next: right-click the selection > Edit > Download Metadata, or use Library > Download Metadata > Selected games.");
            }

            return sb.ToString();
        }

        private static List<uint> ScanFolder(string folder)
        {
            var ids = new SortedSet<uint>();
            foreach (var file in Directory.EnumerateFiles(folder, "*.lua", SearchOption.TopDirectoryOnly))
            {
                if (uint.TryParse(Path.GetFileNameWithoutExtension(file), out var id) && id > 0)
                {
                    ids.Add(id);
                }
            }

            return ids.ToList();
        }

        private static string CleanName(string name)
        {
            return name.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        }

        private bool IsCacheUsable(AppLookup entry)
        {
            if (entry == null)
            {
                return false;
            }

            return entry.Success || (DateTime.UtcNow - entry.CheckedUtc).TotalDays < settings.RetryFailedAfterDays;
        }

        private string CachePath => Path.Combine(GetPluginUserDataPath(), CacheFileName);

        private Dictionary<string, AppLookup> LoadCache()
        {
            try
            {
                if (File.Exists(CachePath))
                {
                    return Serialization.FromJsonFile<Dictionary<string, AppLookup>>(CachePath) ?? new Dictionary<string, AppLookup>();
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Couldn't read lookup cache; starting fresh.");
            }

            return new Dictionary<string, AppLookup>();
        }

        private void SaveCache(Dictionary<string, AppLookup> cache)
        {
            try
            {
                Directory.CreateDirectory(GetPluginUserDataPath());
                File.WriteAllText(CachePath, Serialization.ToJson(cache, true), Encoding.UTF8);
            }
            catch (Exception e)
            {
                logger.Warn(e, "Couldn't save lookup cache.");
            }
        }

        private void ClearCache()
        {
            if (File.Exists(CachePath))
            {
                File.Delete(CachePath);
            }

            PlayniteApi.Dialogs.ShowMessage("Steam lookup cache cleared.", "Steam Lua Importer");
        }
    }
}
