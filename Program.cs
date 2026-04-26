using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FakeSpeedTestServer
{
    class Program
    {
        // Night mode settings
        private static readonly object nightModeLock = new object();
        private static volatile bool isInSleepMode = false;
        private static volatile bool isForceRunRequested = false;
        private static volatile bool isNightModeEnabled = true;
        private static int nightStartHour = 1; // 01:00
        private static int nightEndHour = 6;   // 06:00

        // File size limits (1 MB to 10240 MB = 10 GB)
        private const int MinFileSizeMB = 1;
        private const int MaxFileSizeMB = 10240;

        // White listed paths
        private static readonly string[] WhiteListedPaths = new string[]
        {
            "/",
            "/favicon.ico",
            "/updateBrowserInfo",
            "/748_dark",
            "/style.css",
            "/script.js"
        };

        // Ban manager
        private static BanManager banManager;

        // Random headers
        private static List<string> randomHeaders = new List<string>();
        private static readonly object headerLock = new object();

        // Suspicious user agents
        private static HashSet<string> suspiciousUserAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static FileSystemWatcher fileWatcher;

        // Server components
        private static CancellationTokenSource serverCts;
        private static HttpListener listener;

        // Log file lock
        private static readonly object logLock = new object();
        private static string currentLogFile;

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Fake SpeedTest Server ===");
            Console.WriteLine("Initializing...");

            // Initialize components
            banManager = new BanManager("bans.ini");
            LoadSuspiciousUserAgents();
            InitializeFileWatcher();
            LoadRandomHeaders();
            CreateNewLogFile();

            // Start night mode checker thread
            var nightModeToken = new CancellationTokenSource();
            Task.Run(() => CheckNightModeAsync(nightModeToken.Token));

            // Start HTTP listener
            listener = new HttpListener();
            listener.Prefixes.Add("http://+:5000/");
            listener.Start();
            Console.WriteLine("Server started on http://*:5000/");
            Log("Server started on port 5000");

            serverCts = new CancellationTokenSource();

            // Main loop
            MainLoop().GetAwaiter().GetResult();
        }

        private static async Task MainLoop()
        {
            while (!serverCts.Token.IsCancellationRequested)
            {
                // Check night mode
                lock (nightModeLock)
                {
                    if (isInSleepMode && !isForceRunRequested)
                    {
                        Console.WriteLine("[Night Mode] Server is sleeping. Press 'Y' to force run.");
                        
                        // Wait for night mode to end or force run
                        while (isInSleepMode && !isForceRunRequested)
                        {
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(true);
                                if (key.Key == ConsoleKey.Y)
                                {
                                    lock (nightModeLock)
                                    {
                                        isForceRunRequested = true;
                                    }
                                    Console.WriteLine("[Night Mode] Force run requested!");
                                    break;
                                }
                            }
                            await Task.Delay(1000, serverCts.Token).ConfigureAwait(false);
                        }
                    }
                }

                try
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);
                    _ = HandleRequestAsync(context);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogErrorToFile($"Main loop error: {ex.Message}");
                }
            }

            listener.Stop();
            listener.Close();
            fileWatcher?.Dispose();
            Console.WriteLine("Server stopped.");
            Log("Server stopped");
        }

        private static bool IsNightTime()
        {
            var now = DateTime.Now.Hour;
            if (nightStartHour < nightEndHour)
            {
                return now >= nightStartHour && now < nightEndHour;
            }
            else
            {
                return now >= nightStartHour || now < nightEndHour;
            }
        }

        private static async Task CheckNightModeAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);

                    if (!isNightModeEnabled)
                        continue;

                    bool isNight = IsNightTime();

                    lock (nightModeLock)
                    {
                        if (isNight && !isInSleepMode)
                        {
                            isInSleepMode = true;
                            isForceRunRequested = false;
                            Console.WriteLine("[Night Mode] Entering sleep mode.");
                            Log("Night mode started");
                        }
                        else if (!isNight && isInSleepMode)
                        {
                            isInSleepMode = false;
                            isForceRunRequested = false;
                            Console.WriteLine("[Night Mode] Exiting sleep mode.");
                            Log("Night mode ended");
                        }
                    }

                    // Cleanup old tracking every minute
                    banManager.CleanupOldTracking(60);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogErrorToFile($"Night mode check error: {ex.Message}");
                }
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context)
        {
            var clientIp = context.Request.RemoteEndPoint.Address.ToString();
            var url = context.Request.Url.AbsolutePath;
            var userAgent = context.Request.UserAgent ?? "";

            try
            {
                // Check for secret unban code first
                bool hasSecretCode = url.Contains("748_dark") || 
                                     context.Request.QueryString.ToString().Contains("748_dark") ||
                                     userAgent.Contains("748_dark");

                if (hasSecretCode)
                {
                    banManager.UnbanClient(clientIp);
                    Log($"Secret unban code used by {clientIp}");
                    
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = "/";
                    context.Response.Close();
                    return;
                }

                // Check if IP is banned
                if (banManager.IsBanned(clientIp))
                {
                    Log($"Blocked banned IP: {clientIp}");
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                // Check for suspicious request
                if (IsSuspiciousRequest(context))
                {
                    banManager.BanClient(clientIp, TimeSpan.FromDays(365 * 200000)); // 200,000 years
                    Log($"Banned suspicious IP: {clientIp} - Perma-ban");
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                // Check white list
                if (!IsWhiteListed(url))
                {
                    var tracking = banManager.GetOrCreateClientTracking(clientIp);
                    lock (tracking)
                    {
                        tracking.BadRequestCount++;
                        if (tracking.BadRequestCount >= 1)
                        {
                            banManager.BanClient(clientIp, TimeSpan.FromMinutes(2));
                            Log($"Banned IP {clientIp} for 2 minutes - Unknown path: {url}");
                        }
                    }
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                // Process request normally
                await ProcessRequestNormally(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorToFile($"Request handling error for {clientIp}: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private static bool IsSuspiciousRequest(HttpListenerContext context)
        {
            var userAgent = context.Request.UserAgent ?? "";
            var acceptHeader = context.Request.Headers["Accept"] ?? "";
            var acceptLanguage = context.Request.Headers["Accept-Language"] ?? "";

            // Empty User-Agent
            if (string.IsNullOrEmpty(userAgent))
            {
                return true;
            }

            // Check for suspicious keywords
            foreach (var keyword in suspiciousUserAgents)
            {
                if (userAgent.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            // Accept: */* without Accept-Language
            if (acceptHeader == "*/*" && string.IsNullOrEmpty(acceptLanguage))
            {
                return true;
            }

            return false;
        }

        private static bool IsWhiteListed(string url)
        {
            // Exact matches
            foreach (var path in WhiteListedPaths)
            {
                if (url == path)
                    return true;
            }

            // Download paths: /download/N where N is 1-10240
            if (url.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = url.Split('/');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int size))
                {
                    if (size >= MinFileSizeMB && size <= MaxFileSizeMB)
                        return true;
                }
            }

            return false;
        }

        private static async Task ProcessRequestNormally(HttpListenerContext context)
        {
            var url = context.Request.Url.AbsolutePath;
            var clientIp = context.Request.RemoteEndPoint.Address.ToString();
            var startTime = DateTime.UtcNow;

            // Add random header
            string randomHeader;
            lock (headerLock)
            {
                if (randomHeaders.Count > 0)
                {
                    var rnd = new Random();
                    randomHeader = randomHeaders[rnd.Next(randomHeaders.Count)];
                }
                else
                {
                    randomHeader = "Unknown";
                }
            }
            context.Response.Headers.Add("X-Powered-By", randomHeader);

            // Handle specific paths
            if (url == "/" || url == "/index.html")
            {
                await ServeHomePage(context).ConfigureAwait(false);
            }
            else if (url == "/favicon.ico")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
            else if (url == "/updateBrowserInfo")
            {
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            else if (url == "/style.css")
            {
                await ServeStaticFile(context, "style.css", "text/css").ConfigureAwait(false);
            }
            else if (url == "/script.js")
            {
                await ServeStaticFile(context, "script.js", "application/javascript").ConfigureAwait(false);
            }
            else if (url.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = url.Split('/');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int sizeMB))
                {
                    var endTime = await StreamFakeFileAsync(context, sizeMB).ConfigureAwait(false);
                    var duration = (endTime - startTime).TotalSeconds;
                    if (duration > 0)
                    {
                        var speedMbps = (sizeMB * 8) / duration / 1000000; // Mbit/s
                        Log($"Download completed: {sizeMB}MB to {clientIp} in {duration:F2}s ({speedMbps:F2} Mbit/s)");
                    }
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            else
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }

        private static async Task<DateTime> StreamFakeFileAsync(HttpListenerContext context, int sizeMB)
        {
            var response = context.Response;
            var fileName = $"fake_file_{sizeMB}MB.dat";
            
            response.ContentType = "application/octet-stream";
            response.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");
            
            long totalBytes = (long)sizeMB * 1024 * 1024;
            response.ContentLength64 = totalBytes;

            var marker = Encoding.UTF8.GetBytes("ТЕСТ");
            var buffer = new byte[1024 * 1024]; // 1 MB buffer

            using (var output = response.OutputStream)
            {
                // Write start marker
                await output.WriteAsync(marker, 0, marker.Length).ConfigureAwait(false);
                
                // Calculate remaining bytes after markers
                long remainingBytes = totalBytes - (marker.Length * 2);
                
                // Write zero-filled data
                while (remainingBytes > buffer.Length)
                {
                    await output.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    remainingBytes -= buffer.Length;
                }
                
                if (remainingBytes > 0)
                {
                    await output.WriteAsync(buffer, 0, (int)remainingBytes).ConfigureAwait(false);
                }
                
                // Write end marker
                await output.WriteAsync(marker, 0, marker.Length).ConfigureAwait(false);
            }

            return DateTime.UtcNow;
        }

        private static async Task ServeStaticFile(HttpListenerContext context, string fileName, string contentType)
        {
            if (!File.Exists(fileName))
            {
                LogErrorToFile($"{fileName} not found");
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var response = context.Response;
            response.ContentType = contentType;
            var fileContent = File.ReadAllText(fileName);
            var buffer = Encoding.UTF8.GetBytes(fileContent);
            response.ContentLength64 = buffer.Length;
            
            using (var output = response.OutputStream)
            {
                await output.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            }
        }

        private static async Task ServeHomePage(HttpListenerContext context)
        {
            var filePath = "index.html";
            string html;
            
            if (!File.Exists(filePath))
            {
                LogErrorToFile($"index.html not found");
                context.Response.StatusCode = 500;
                context.Response.Close();
                return;
            }
            
            html = File.ReadAllText(filePath);

            var response = context.Response;
            response.ContentType = "text/html; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            using (var output = response.OutputStream)
            {
                await output.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            }
        }

        private static void LoadSuspiciousUserAgents()
        {
            var filePath = "suspicious_agents.txt";
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Warning: {filePath} not found. Using defaults.");
                suspiciousUserAgents = new HashSet<string>(new string[]
                {
                    "curl", "wget", "python", "scanner", "bot", "spider", "crawler",
                    "zgrab", "go-http-client", "internetmeasurement", "palo alto networks",
                    "masscan", "nmap", "nikto", "sqlmap"
                }, StringComparer.OrdinalIgnoreCase);
                return;
            }

            try
            {
                var lines = File.ReadAllLines(filePath);
                suspiciousUserAgents.Clear();
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                    {
                        suspiciousUserAgents.Add(trimmed);
                    }
                }
                Console.WriteLine($"Loaded {suspiciousUserAgents.Count} suspicious user agents.");
            }
            catch (Exception ex)
            {
                LogErrorToFile($"Error loading suspicious agents: {ex.Message}");
            }
        }

        private static void InitializeFileWatcher()
        {
            var filePath = Path.GetFullPath("suspicious_agents.txt");
            var directory = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileName(filePath);

            fileWatcher = new FileSystemWatcher(directory, fileName);
            fileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            
            fileWatcher.Changed += (sender, e) =>
            {
                // Debounce - wait 100ms before reloading
                Thread.Sleep(100);
                Console.WriteLine("Reloading suspicious_agents.txt...");
                LoadSuspiciousUserAgents();
                Log("Reloaded suspicious_agents.txt");
            };

            fileWatcher.EnableRaisingEvents = true;
            Console.WriteLine($"Watching {filePath} for changes.");
        }

        private static void LoadRandomHeaders()
        {
            var filePath = "random_headers.txt";
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Warning: {filePath} not found.");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(filePath);
                lock (headerLock)
                {
                    randomHeaders.Clear();
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            randomHeaders.Add(trimmed);
                        }
                    }
                }
                Console.WriteLine($"Loaded {randomHeaders.Count} random headers.");
            }
            catch (Exception ex)
            {
                LogErrorToFile($"Error loading random headers: {ex.Message}");
            }
        }

        private static void CreateNewLogFile()
        {
            var timestamp = DateTime.Now.ToString("dd-MM-yyyy_HH-mm-ss");
            currentLogFile = $"server_log_{timestamp}.txt";
            
            // Create empty log file with header
            File.WriteAllText(currentLogFile, $"=== Log started at {DateTime.Now:dd-MM-yyyy HH:mm:ss} ===\r\n", Encoding.UTF8);
        }

        private static void Log(string message)
        {
            var timestamp = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
            var logEntry = $"[{timestamp}] {message}";
            
            Console.WriteLine(logEntry);
            
            lock (logLock)
            {
                try
                {
                    File.AppendAllText(currentLogFile, logEntry + "\r\n", Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing to log: {ex.Message}");
                }
            }
        }

        private static void LogErrorToFile(string message)
        {
            var timestamp = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
            var logEntry = $"[{timestamp}] ERROR: {message}";
            
            Console.WriteLine(logEntry);
            
            lock (logLock)
            {
                try
                {
                    File.AppendAllText(currentLogFile, logEntry + "\r\n", Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing error to log: {ex.Message}");
                }
            }
        }
    }

    public class BanManager
    {
        private readonly string _banFile;
        private readonly ConcurrentDictionary<string, DateTime> _bannedClients;
        private readonly ConcurrentDictionary<string, ClientTracking> _clientTracking;
        private readonly object _lock = new object();

        public BanManager(string banFile)
        {
            _banFile = banFile;
            _bannedClients = new ConcurrentDictionary<string, DateTime>();
            _clientTracking = new ConcurrentDictionary<string, ClientTracking>();
            LoadBans();
        }

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
                    // Ban expired
                    _bannedClients.TryRemove(ip, out _);
                    SaveBans();
                }
            }
            return false;
        }

        public void BanClient(string ip, TimeSpan duration)
        {
            var expiry = DateTime.Now.Add(duration);
            _bannedClients[ip] = expiry;
            SaveBans();
        }

        public void UnbanClient(string ip)
        {
            _bannedClients.TryRemove(ip, out _);
            SaveBans();
        }

        public ClientTracking GetOrCreateClientTracking(string ip)
        {
            return _clientTracking.GetOrAdd(ip, _ => new ClientTracking());
        }

        public void CleanupOldTracking(int maxAgeMinutes)
        {
            var cutoff = DateTime.Now.AddMinutes(-maxAgeMinutes);
            foreach (var kvp in _clientTracking.ToList())
            {
                // Simple cleanup - could be enhanced based on actual usage
                // For now, we just ensure the dictionary doesn't grow indefinitely
            }
        }

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

    public class ClientTracking
    {
        public int BadRequestCount { get; set; }
        public string FullBrowserVersion { get; set; }
        public string ClientBrowserName { get; set; }
    }
}
