using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Handles incoming HTTP requests for the fake speed test server.
    /// Manages request routing, ban checking, suspicious request detection,
    /// and response generation including file streaming and static file serving.
    /// </summary>
    public class RequestHandler
    {
        private readonly BanManager _banManager;
        private readonly NightModeService _nightModeService;
        private readonly List<string> _randomHeaders;
        private readonly object _headerLock;
        private readonly HashSet<string> _suspiciousUserAgents;
        private readonly Action<string> _logAction;
        private readonly Action<string> _logErrorAction;

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

        /// <summary>
        /// Initializes a new instance of the RequestHandler class.
        /// </summary>
        /// <param name="banManager">BanManager instance for ban operations</param>
        /// <param name="nightModeService">NightModeService instance for sleep mode checks</param>
        /// <param name="randomHeaders">List of random headers for X-Powered-By response</param>
        /// <param name="headerLock">Lock object for thread-safe header access</param>
        /// <param name="suspiciousUserAgents">Set of suspicious user agent strings</param>
        /// <param name="logAction">Action for logging info messages</param>
        /// <param name="logErrorAction">Action for logging error messages</param>
        public RequestHandler(
            BanManager banManager,
            NightModeService nightModeService,
            List<string> randomHeaders,
            object headerLock,
            HashSet<string> suspiciousUserAgents,
            Action<string> logAction,
            Action<string> logErrorAction)
        {
            _banManager = banManager;
            _nightModeService = nightModeService;
            _randomHeaders = randomHeaders;
            _headerLock = headerLock;
            _suspiciousUserAgents = suspiciousUserAgents;
            _logAction = logAction;
            _logErrorAction = logErrorAction;
        }

        /// <summary>
        /// Main entry point for handling HTTP requests.
        /// Performs ban checks, suspicious request detection, whitelist validation,
        /// and routes to appropriate handler methods.
        /// </summary>
        /// <param name="context">HTTP listener context containing request/response</param>
        /// <returns>Task representing the asynchronous operation</returns>
        public async Task HandleRequestAsync(HttpListenerContext context)
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
                    _banManager.UnbanClient(clientIp);
                    _logAction?.Invoke($"Secret unban code used by {clientIp}");
                    
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = "/";
                    context.Response.Close();
                    return;
                }

                // Check if IP is banned
                if (_banManager.IsBanned(clientIp))
                {
                    _logAction?.Invoke($"Blocked banned IP: {clientIp}");
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                // Check for suspicious request
                if (IsSuspiciousRequest(context))
                {
                    _banManager.BanClient(clientIp, TimeSpan.FromDays(365 * 10)); // 10 years
                    _logAction?.Invoke($"Banned suspicious IP: {clientIp} - 10 year ban");
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                // Check white list
                if (!IsWhiteListed(url))
                {
                    var tracking = _banManager.GetOrCreateClientTracking(clientIp);
                    lock (tracking)
                    {
                        tracking.BadRequestCount++;
                        if (tracking.BadRequestCount >= 1)
                        {
                            _banManager.BanClient(clientIp, TimeSpan.FromDays(365 * 10)); // 10 years
                            _logAction?.Invoke($"Banned IP {clientIp} for 10 years - Unknown path: {url}");
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
                _logErrorAction?.Invoke($"Request handling error for {clientIp}: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        /// <summary>
        /// Detects suspicious requests based on user agent, accept headers,
        /// and known malicious patterns.
        /// </summary>
        /// <param name="context">HTTP listener context</param>
        /// <returns>True if request is suspicious, false otherwise</returns>
        private bool IsSuspiciousRequest(HttpListenerContext context)
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
            foreach (var keyword in _suspiciousUserAgents)
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

        /// <summary>
        /// Checks if a URL path is in the whitelist of allowed paths.
        /// Includes exact matches and dynamic download paths (/download/N).
        /// </summary>
        /// <param name="url">URL path to check</param>
        /// <returns>True if URL is whitelisted, false otherwise</returns>
        private bool IsWhiteListed(string url)
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

        /// <summary>
        /// Processes a validated request by routing to the appropriate handler.
        /// Adds random X-Powered-By header to all responses.
        /// </summary>
        /// <param name="context">HTTP listener context</param>
        /// <returns>Task representing the asynchronous operation</returns>
        private async Task ProcessRequestNormally(HttpListenerContext context)
        {
            var url = context.Request.Url.AbsolutePath;
            var clientIp = context.Request.RemoteEndPoint.Address.ToString();
            var startTime = DateTime.UtcNow;

            // Track connection
            var tracking = _banManager.GetOrCreateClientTracking(clientIp);
            lock (tracking)
            {
                tracking.ConnectionCount++;
                tracking.LastConnectionTime = DateTime.Now;
            }

            // Add random header
            string randomHeader;
            lock (_headerLock)
            {
                if (_randomHeaders.Count > 0)
                {
                    var rnd = new Random();
                    randomHeader = _randomHeaders[rnd.Next(_randomHeaders.Count)];
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
                        _logAction?.Invoke($"Download completed: {sizeMB}MB to {clientIp} in {duration:F2}s ({speedMbps:F2} Mbit/s)");
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

        /// <summary>
        /// Streams a fake file of specified size to the client.
        /// File contains "ТЕСТ" markers at start and end with zero-filled data in between.
        /// </summary>
        /// <param name="context">HTTP listener context</param>
        /// <param name="sizeMB">Size of the file in megabytes</param>
        /// <returns>DateTime when streaming completed</returns>
        private async Task<DateTime> StreamFakeFileAsync(HttpListenerContext context, int sizeMB)
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

        /// <summary>
        /// Serves a static file from the current directory.
        /// Returns 404 if file doesn't exist.
        /// </summary>
        /// <param name="context">HTTP listener context</param>
        /// <param name="fileName">Name of the file to serve</param>
        /// <param name="contentType">MIME type of the file</param>
        /// <returns>Task representing the asynchronous operation</returns>
        private async Task ServeStaticFile(HttpListenerContext context, string fileName, string contentType)
        {
            if (!File.Exists(fileName))
            {
                _logErrorAction?.Invoke($"{fileName} not found");
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

        /// <summary>
        /// Serves the main HTML page (index.html).
        /// Returns 500 error if file doesn't exist.
        /// </summary>
        /// <param name="context">HTTP listener context</param>
        /// <returns>Task representing the asynchronous operation</returns>
        private async Task ServeHomePage(HttpListenerContext context)
        {
            var filePath = "index.html";
            string html;
            
            if (!File.Exists(filePath))
            {
                _logErrorAction?.Invoke("index.html not found");
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
    }
}
