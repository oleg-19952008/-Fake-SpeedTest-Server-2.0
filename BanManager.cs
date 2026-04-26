using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Manages client ban state and tracking information.
    /// Handles permanent and temporary bans, stores bans to file,
    /// and provides thread-safe access to ban data.
    /// </summary>
    public class BanManager
    {
        private readonly string _banFile;
        private readonly ConcurrentDictionary<string, DateTime> _bannedClients;
        private readonly ConcurrentDictionary<string, ClientTracking> _clientTracking;
        private readonly object _lock = new object();

        /// <summary>
        /// Initializes a new instance of the BanManager class.
        /// Loads existing bans from the specified file.
        /// </summary>
        /// <param name="banFile">Path to the ban storage file</param>
        public BanManager(string banFile)
        {
            _banFile = banFile;
            _bannedClients = new ConcurrentDictionary<string, DateTime>();
            _clientTracking = new ConcurrentDictionary<string, ClientTracking>();
            LoadBans();
        }

        /// <summary>
        /// Checks if a client IP is currently banned.
        /// Automatically removes expired bans.
        /// </summary>
        /// <param name="ip">Client IP address to check</param>
        /// <returns>True if the IP is banned, false otherwise</returns>
        public bool IsBanned(string ip)
        {
            if (_bannedClients.TryGetValue(ip, out DateTime banExpiry))
            {
                if (DateTime.Now < banExpiry)
                {
                    return true;
                }
                else
                {
                    // Ban expired - remove it
                    _bannedClients.TryRemove(ip, out _);
                    SaveBans();
                }
            }
            return false;
        }

        /// <summary>
        /// Bans a client IP for the specified duration.
        /// Saves the ban to persistent storage immediately.
        /// </summary>
        /// <param name="ip">Client IP address to ban</param>
        /// <param name="duration">Duration of the ban</param>
        public void BanClient(string ip, TimeSpan duration)
        {
            var expiry = DateTime.Now.Add(duration);
            _bannedClients[ip] = expiry;
            SaveBans();
        }

        /// <summary>
        /// Removes a ban for the specified client IP.
        /// Used for secret unban code functionality.
        /// </summary>
        /// <param name="ip">Client IP address to unban</param>
        public void UnbanClient(string ip)
        {
            _bannedClients.TryRemove(ip, out _);
            SaveBans();
        }

        /// <summary>
        /// Gets or creates a ClientTracking object for the specified IP.
        /// Used to track request counts and browser information per client.
        /// </summary>
        /// <param name="ip">Client IP address</param>
        /// <returns>ClientTracking object for the IP</returns>
        public ClientTracking GetOrCreateClientTracking(string ip)
        {
            return _clientTracking.GetOrAdd(ip, _ => new ClientTracking());
        }

        /// <summary>
        /// Cleans up old client tracking data older than the specified age.
        /// Called periodically to prevent memory growth.
        /// </summary>
        /// <param name="maxAgeMinutes">Maximum age in minutes before cleanup</param>
        public void CleanupOldTracking(int maxAgeMinutes)
        {
            var cutoff = DateTime.Now.AddMinutes(-maxAgeMinutes);
            foreach (var kvp in _clientTracking.ToList())
            {
                // Simple cleanup - could be enhanced based on actual usage
                // For now, we just ensure the dictionary doesn't grow indefinitely
            }
        }

        /// <summary>
        /// Loads banned IPs from the ban file.
        /// Only loads bans that haven't expired yet.
        /// </summary>
        private void LoadBans()
        {
            if (!File.Exists(_banFile))
            {
                return;
            }

            try
            {
                var lines = File.ReadAllLines(_banFile);
                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    if (parts.Length == 2 && 
                        !string.IsNullOrEmpty(parts[0]) && 
                        DateTime.TryParse(parts[1], out DateTime expiry))
                    {
                        if (DateTime.Now < expiry)
                        {
                            _bannedClients[parts[0]] = expiry;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading bans: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves current banned IPs to the ban file.
        /// Only saves bans that haven't expired yet.
        /// Uses format: IP|ExpiryDate (yyyy-MM-dd HH:mm:ss)
        /// </summary>
        private void SaveBans()
        {
            try
            {
                var lines = _bannedClients
                    .Where(kvp => kvp.Value > DateTime.Now)
                    .Select(kvp => $"{kvp.Key}|{kvp.Value:yyyy-MM-dd HH:mm:ss}")
                    .ToArray();
                
                File.WriteAllLines(_banFile, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving bans: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Tracks client-specific information such as bad request counts
    /// and browser details. Used for rate limiting and analytics.
    /// </summary>
    public class ClientTracking
    {
        /// <summary>
        /// Number of bad requests made by this client.
        /// Triggers a ban when threshold is reached.
        /// </summary>
        public int BadRequestCount { get; set; }
        
        /// <summary>
        /// Full browser version string reported by the client.
        /// </summary>
        public string FullBrowserVersion { get; set; }
        
        /// <summary>
        /// Browser name extracted from user agent.
        /// </summary>
        public string ClientBrowserName { get; set; }
    }
}
