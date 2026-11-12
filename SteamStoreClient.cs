using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;

namespace SteamLuaImporter
{
    /// <summary>
    /// Looks up an app ID's name and type with Steam's store API (one app per request).
    /// </summary>
    public class SteamStoreClient
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string Url = "https://store.steampowered.com/api/appdetails?appids={0}&filters=basic&l=english";
        private const int MaxAttempts = 5;
        private const int BackoffMs = 60_000;

        private class AppDetailsEntry
        {
            public bool success { get; set; }
            public AppDetailsData data { get; set; }
        }

        private class AppDetailsData
        {
            public string type { get; set; }
            public string name { get; set; }
        }

        static SteamStoreClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        /// <summary>
        /// Returns the lookup result, or null if cancelled or Steam could not be reached after retries
        /// (null results are not cached, so the ID is tried again next run).
        /// </summary>
        public AppLookup Lookup(uint appId, CancellationToken cancel)
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (cancel.IsCancellationRequested)
                {
                    return null;
                }

                try
                {
                    string json;
                    using (var web = new WebClient())
                    {
                        web.Encoding = Encoding.UTF8;
                        web.Headers[HttpRequestHeader.UserAgent] = "Playnite SteamLuaImporter/1.0";
                        json = web.DownloadString(string.Format(Url, appId));
                    }

                    return Parse(appId, json);
                }
                catch (WebException e) when (IsRateLimit(e))
                {
                    logger.Warn($"Steam store rate limit hit on {appId} (attempt {attempt}), waiting {BackoffMs / 1000}s.");
                    if (cancel.WaitHandle.WaitOne(BackoffMs))
                    {
                        return null;
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Steam store lookup failed for {appId} (attempt {attempt}).");
                    if (cancel.WaitHandle.WaitOne(5_000))
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        private static AppLookup Parse(uint appId, string json)
        {
            var result = new AppLookup { CheckedUtc = DateTime.UtcNow };

            // Unknown IDs can come back as the literal "null".
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
            {
                return result;
            }

            var root = Serialization.FromJson<Dictionary<string, AppDetailsEntry>>(json);
            if (root != null
                && root.TryGetValue(appId.ToString(), out var entry)
                && entry != null
                && entry.success
                && entry.data != null
                && !string.IsNullOrWhiteSpace(entry.data.name))
            {
                result.Success = true;
                result.Type = entry.data.type?.Trim().ToLowerInvariant();
                result.Name = entry.data.name.Trim();
            }

            return result;
        }

        private static bool IsRateLimit(WebException e)
        {
            if (e.Response is HttpWebResponse response)
            {
                var code = (int)response.StatusCode;
                return code == 429 || code == 403;
            }

            return false;
        }
    }
}
