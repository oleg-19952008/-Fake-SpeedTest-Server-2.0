using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Обрабатывает входящие HTTP-запросы для поддельного сервера проверки скорости.
    /// Управляет маршрутизацией запросов, проверкой банов, обнаружением подозрительных запросов,
    /// а также генерацией ответов, включая потоковую передачу файлов и обслуживание статических файлов.
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
        private readonly string _baseDirectory;

        // File size limits (1 MB to 10240 MB = 10 GB)
        private const int MinFileSizeMB = 1;
        private const int MaxFileSizeMB = 10240;

        // White listed paths
        private static readonly string[] WhiteListedPaths = new string[]
        {
            "/",
            "/favicon.ico",
            "/updateBrowserInfo",
            "/ping",
            "/748_dark",
            "/style.css",
            "/script.js",
            "/admin.html",
            "/admin.js"
        };

        /// <summary>
        /// Инициализирует новый экземпляр класса RequestHandler.
        /// </summary>
        /// <param name="banManager">Экземпляр BanManager для операций с банами</param>
        /// <param name="nightModeService">Экземпляр NightModeService для проверки ночного режима</param>
        /// <param name="randomHeaders">Список случайных заголовков для ответа X-Powered-By</param>
        /// <param name="headerLock">Объект блокировки для потокобезопасного доступа к заголовкам</param>
        /// <param name="suspiciousUserAgents">Набор строк подозрительных пользовательских агентов</param>
        /// <param name="logAction">Действие для логирования информационных сообщений</param>
        /// <param name="logErrorAction">Действие для логирования сообщений об ошибках</param>
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
            _baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        }

        /// <summary>
        /// Главная точка входа для обработки HTTP-запросов.
        /// Выполняет проверки банов, обнаружение подозрительных запросов, проверку белого списка,
        /// и маршрутизирует к соответствующим методам обработчиков.
        /// </summary>
        /// <param name="context">Контекст HTTP-слушателя, содержащий запрос/ответ</param>
        /// <returns>Задача, представляющая асинхронную операцию</returns>
        public async Task HandleRequestAsync(HttpListenerContext context)
        {
            var clientIp = context.Request.RemoteEndPoint.Address.ToString();
            var url = context.Request.Url.AbsolutePath;
            var userAgent = context.Request.UserAgent ?? "";

            try
            {
                // Check if IP is banned FIRST - before any parsing or logging of headers
                if (_banManager.IsBanned(clientIp))
                {
                    // Instantly close connection without any response for banned IPs
                    try { context.Response.Close(); } catch { }
                    return;
                }

                // Log incoming request with FULL user agent
                int threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                _logAction?.Invoke($"[{threadId:D2}] Входящий запрос от {clientIp}: {url} (UA: {(string.IsNullOrEmpty(userAgent) ? "пустой" : userAgent)})");

                // Check for secret unban code first
                bool hasSecretCode = url.Contains("748_dark") || 
                                     context.Request.QueryString.ToString().Contains("748_dark") ||
                                     userAgent.Contains("748_dark");

                if (hasSecretCode)
                {
                    _banManager.UnbanClient(clientIp);
                    threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    _logAction?.Invoke($"[{threadId:D2}] Секретный код разблокировки использован {clientIp}");
                    
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = "/";
                    context.Response.Close();
                    return;
                }

                // Check for suspicious request
                if (IsSuspiciousRequest(context))
                {
                    _banManager.BanClient(clientIp, TimeSpan.FromDays(365 * 10)); // 10 years
                    threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    _logAction?.Invoke($"[{threadId:D2}] Забанен подозрительный IP: {clientIp} - бан на 10 лет");
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
                            threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                            _logAction?.Invoke($"[{threadId:D2}] Забанен IP {clientIp} на 10 лет - Неизвестный путь: {url}");
                        }
                    }
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                // Process request normally
                await ProcessRequestNormally(context).ConfigureAwait(false);
            }
            catch (HttpListenerException hex) when (hex.ErrorCode == 64 || hex.Message.Contains("сетевое имя")) // Network name no longer available
            {
             int   threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                _logAction?.Invoke($"[{threadId:D2}] Клиент {clientIp} неожиданно отключился во время запроса к {url}: {hex.Message}");
                context.Response.StatusCode = 500;
                try { context.Response.Close(); } catch { }
            }
            catch (Exception ex)
            {
                _logErrorAction?.Invoke($"Ошибка обработки запроса для {clientIp} ({url}): {ex.Message}");
                context.Response.StatusCode = 500;
                try { context.Response.Close(); } catch { }
            }
        }

        /// <summary>
        /// Обнаруживает подозрительные запросы на основе пользовательского агента, заголовков Accept,
        /// и известных вредоносных шаблонов.
        /// </summary>
        /// <param name="context">Контекст HTTP-слушателя</param>
        /// <returns>True, если запрос подозрительный, иначе false</returns>
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
        /// Проверяет, находится ли путь URL в белом списке разрешённых путей.
        /// Включает точные совпадения и динамические пути загрузки (/download/N).
        /// </summary>
        /// <param name="url">Путь URL для проверки</param>
        /// <returns>True, если URL в белом списке, иначе false</returns>
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
        /// Обрабатывает проверенный запрос, маршрутизируя его к соответствующему обработчику.
        /// Добавляет случайный заголовок X-Powered-By ко всем ответам.
        /// </summary>
        /// <param name="context">Контекст HTTP-слушателя</param>
        /// <returns>Задача, представляющая асинхронную операцию</returns>
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
                // Возвращаем информацию о браузере включая IP адрес клиента
                // Сначала пробуем получить реальный IP из заголовков прокси
                var clientIpAddress = context.Request.Headers["X-Forwarded-For"] ?? 
                                      context.Request.Headers["X-Real-IP"] ?? 
                                      context.Request.RemoteEndPoint.Address.ToString();
                
                // Если X-Forwarded-For содержит несколько адресов, берем первый (реальный IP клиента)
                if (!string.IsNullOrEmpty(context.Request.Headers["X-Forwarded-For"]))
                {
                    var forwardedIps = context.Request.Headers["X-Forwarded-For"].Split(',');
                    if (forwardedIps.Length > 0)
                    {
                        clientIpAddress = forwardedIps[0].Trim();
                    }
                }
                
                var userAgent = context.Request.UserAgent ?? "Не определен";
                var platform = context.Request.Headers["Sec-Ch-Ua-Platform"] ?? 
                               (userAgent.Contains("Windows") ? "Win32" : 
                                userAgent.Contains("Mac") ? "macOS" : 
                                userAgent.Contains("Linux") ? "Linux" : "Unknown");
                
                var responseInfo = $@"{{
  ""userAgent"": ""{userAgent.Replace("\"", "\\\"")}"",
  ""platform"": ""{platform}"",
  ""ipAddress"": ""{clientIpAddress}""
}}";
                
                var buffer = Encoding.UTF8.GetBytes(responseInfo);
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = buffer.Length;
                using (var output = context.Response.OutputStream)
                {
                    await output.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
                context.Response.Close();
            }
            else if (url == "/ping")
            {
                // Ping endpoint - returns minimal JSON response with 200 OK
                var responseJson = "{\"status\":\"ok\"}";
                var buffer = Encoding.UTF8.GetBytes(responseJson);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                using (var output = context.Response.OutputStream)
                {
                    await output.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
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
            else if (url == "/admin.html")
            {
                await ServeStaticFile(context, "admin.html", "text/html").ConfigureAwait(false);
            }
            else if (url == "/admin.js")
            {
                await ServeStaticFile(context, "admin.js", "application/javascript").ConfigureAwait(false);
            }
            else if (url.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = url.Split('/');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int sizeMB))
                {
                    int threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    _logAction?.Invoke($"[{threadId:D2}] Загрузка началась: {sizeMB}МБ запрошено {clientIp}");
                    var endTime = await StreamFakeFileAsync(context, sizeMB).ConfigureAwait(false);
                    var duration = (endTime - startTime).TotalSeconds;
                    if (duration > 0)
                    {
                        var speedMbps = (sizeMB * 8) / duration / 1000000; // Mbit/s
                        _logAction?.Invoke($"[{threadId:D2}] Загрузка завершена: {sizeMB}МБ для {clientIp} за {duration:F2}с ({speedMbps:F2} Мбит/с)");
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
        /// Потоково передаёт поддельный файл указанного размера клиенту.
        /// Файл содержит маркеры "ТЕСТ" в начале и конце с данными, заполненными нулями между ними.
        /// </summary>
        /// <param name="context">Контекст HTTP-слушателя</param>
        /// <param name="sizeMB">Размер файла в мегабайтах</param>
        /// <returns>DateTime завершения потоковой передачи</returns>
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
        /// Обслуживает статический файл из текущего каталога.
        /// Возвращает 404, если файл не существует.
        /// </summary>
        /// <param name="context">Контекст HTTP-слушателя</param>
        /// <param name="fileName">Имя файла для обслуживания</param>
        /// <param name="contentType">MIME-тип файла</param>
        /// <returns>Задача, представляющая асинхронную операцию</returns>
        private async Task ServeStaticFile(HttpListenerContext context, string fileName, string contentType)
        {
            // Path Traversal Protection: Validate and resolve the full path
            var fullPath = Path.GetFullPath(Path.Combine(_baseDirectory, fileName));
            
            // Ensure the resolved path is within the base directory
            if (!fullPath.StartsWith(_baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _logErrorAction?.Invoke($"Попытка Path Traversal атака: {fileName}");
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }
            
            if (!File.Exists(fullPath))
            {
                _logErrorAction?.Invoke($"{fileName} не найден");
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var response = context.Response;
            response.ContentType = contentType;
            var fileContent = File.ReadAllText(fullPath);
            var buffer = Encoding.UTF8.GetBytes(fileContent);
            
            // Check if client accepts gzip encoding
            string acceptEncoding = context.Request.Headers["Accept-Encoding"] ?? "";
            bool useGzip = acceptEncoding.Contains("gzip");
            
            if (useGzip)
            {
                using (var compressedStream = new MemoryStream())
                {
                    using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Compress, leaveOpen: true))
                    {
                        await gzipStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    }
                    var compressedBuffer = compressedStream.ToArray();
                    
                    response.Headers.Add("Content-Encoding", "gzip");
                    response.ContentLength64 = compressedBuffer.Length;
                    
                    using (var output = response.OutputStream)
                    {
                        await output.WriteAsync(compressedBuffer, 0, compressedBuffer.Length).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                response.ContentLength64 = buffer.Length;
                
                using (var output = response.OutputStream)
                {
                    await output.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Обслуживает главную HTML-страницу (index.html).
        /// Возвращает ошибку 500, если файл не существует.
        /// </summary>
        /// <param name="context">Контекст HTTP-слушателя</param>
        /// <returns>Задача, представляющая асинхронную операцию</returns>
        private async Task ServeHomePage(HttpListenerContext context)
        {
            // Path Traversal Protection: Validate and resolve the full path
            var filePath = Path.GetFullPath(Path.Combine(_baseDirectory, "index.html"));
            
            // Ensure the resolved path is within the base directory
            if (!filePath.StartsWith(_baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _logErrorAction?.Invoke("Попытка Path Traversal атака: index.html");
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }
            
            string html;
            
            if (!File.Exists(filePath))
            {
                _logErrorAction?.Invoke("index.html не найден");
                context.Response.StatusCode = 500;
                context.Response.Close();
                return;
            }
            
            html = File.ReadAllText(filePath);

            var response = context.Response;
            response.ContentType = "text/html; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(html);
            
            // Check if client accepts gzip encoding
            string acceptEncoding = context.Request.Headers["Accept-Encoding"] ?? "";
            bool useGzip = acceptEncoding.Contains("gzip");
            
            if (useGzip)
            {
                using (var compressedStream = new MemoryStream())
                {
                    using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Compress, leaveOpen: true))
                    {
                        await gzipStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    }
                    var compressedBuffer = compressedStream.ToArray();
                    
                    response.Headers.Add("Content-Encoding", "gzip");
                    response.ContentLength64 = compressedBuffer.Length;
                    
                    using (var output = response.OutputStream)
                    {
                        await output.WriteAsync(compressedBuffer, 0, compressedBuffer.Length).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                response.ContentLength64 = buffer.Length;
                using (var output = response.OutputStream)
                {
                    await output.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
            }
        }
    }
}
