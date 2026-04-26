using System;
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
    /// <summary>
    /// Main entry point for the Fake SpeedTest Server application.
    /// Orchestrates all server components including ban management, night mode,
    /// request handling, logging, and configuration loading.
    /// Version: 2.2
    /// </summary>
    class Program
    {
        // Server components
        private static BanManager banManager;
        private static NightModeService nightModeService;
        private static RequestHandler requestHandler;
        private static CancellationTokenSource serverCts;
        private static HttpListener listener;

        // Random headers
        private static List<string> randomHeaders = new List<string>();
        private static readonly object headerLock = new object();

        // Suspicious user agents
        private static HashSet<string> suspiciousUserAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static FileSystemWatcher fileWatcher;

        // Log file lock
        private static readonly object logLock = new object();
        private static string currentLogFile;

        /// <summary>
        /// Main entry point of the application.
        /// Initializes all components and starts the HTTP server.
        /// </summary>
        /// <param name="args">Command line arguments (not used)</param>
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Fake SpeedTest Server v2.2 ===");
            Console.WriteLine("Initializing...");

            // Initialize components
            banManager = new BanManager("bans.ini");
            LoadSuspiciousUserAgents();
            InitializeFileWatcher();
            LoadRandomHeaders();
            CreateNewLogFile();

            // Initialize night mode service
            nightModeService = new NightModeService(banManager, Log);
            nightModeService.Start();

            // Initialize request handler
            requestHandler = new RequestHandler(
                banManager,
                nightModeService,
                randomHeaders,
                headerLock,
                suspiciousUserAgents,
                Log,
                LogErrorToFile);

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

        /// <summary>
        /// Main server loop that accepts and processes incoming HTTP requests.
        /// Handles night mode sleep states and graceful shutdown.
        /// Updates window title with connection count every 30 seconds.
        /// </summary>
        private static async Task MainLoop()
        {
            var lastTitleUpdate = DateTime.MinValue;
            
            while (!serverCts.Token.IsCancellationRequested)
            {
                // Check night mode
                if (nightModeService.IsInSleepMode)
                {
                    await nightModeService.WaitForNightModeEndOrForceRun(serverCts);
                }

                // Update window title with connection count every 30 seconds
                if ((DateTime.Now - lastTitleUpdate).TotalSeconds >= 30)
                {
                    int connections = banManager.GetConnectionsLast24Hours();
                    Console.Title = $"Fake SpeedTest Server v2.2 - Connections (24h): {connections}";
                    lastTitleUpdate = DateTime.Now;
                }

                try
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);
                    _ = requestHandler.HandleRequestAsync(context);
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
            nightModeService?.Stop();
            Console.WriteLine("Server stopped.");
            Log("Server stopped");
        }

        /// <summary>
        /// Loads suspicious user agents from file or uses defaults.
        /// Monitored by FileSystemWatcher for hot reload capability.
        /// </summary>
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

        /// <summary>
        /// Initializes FileSystemWatcher to monitor suspicious_agents.txt for changes.
        /// Automatically reloads the list when the file is modified.
        /// </summary>
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

        /// <summary>
        /// Loads random headers from file for X-Powered-By response header randomization.
        /// </summary>
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

        /// <summary>
        /// Creates a new log file with timestamp in dd-MM-yyyy format.
        /// Called at server startup to begin logging session.
        /// </summary>
        private static void CreateNewLogFile()
        {
            var timestamp = DateTime.Now.ToString("dd-MM-yyyy_HH-mm-ss");
            currentLogFile = $"server_log_{timestamp}.txt";
            
            // Create empty log file with header
            File.WriteAllText(currentLogFile, $"=== Log started at {DateTime.Now:dd-MM-yyyy HH:mm:ss} ===\r\n", Encoding.UTF8);
        }

        /// <summary>
        /// Logs an informational message to console and current log file.
        /// Thread-safe using lock on logLock object.
        /// </summary>
        /// <param name="message">Message to log</param>
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

        /// <summary>
        /// Logs an error message to console and current log file.
        /// Thread-safe using lock on logLock object.
        /// </summary>
        /// <param name="message">Error message to log</param>
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
}
