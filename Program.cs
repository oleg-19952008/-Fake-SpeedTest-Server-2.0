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
    /// </summary>
    class Program
    {
        // Глобальная константа версии
        private const string AppVersion = "2.11.0";
        
        // Компоненты сервера
        private static BanManager banManager;
        private static NightModeService nightModeService;
        private static RequestHandler requestHandler;
        private static CancellationTokenSource serverCts;
        private static HttpListener listener;
        private static bool isShuttingDown = false;

        // Случайные заголовки
        private static List<string> randomHeaders = new List<string>();
        private static readonly object headerLock = new object();

        // Подозрительные пользовательские агенты
        private static HashSet<string> suspiciousUserAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static FileSystemWatcher fileWatcher;

        // Блокировка для лог-файла
        private static readonly object logLock = new object();
        private static string currentLogFile;

        /// <summary>
        /// Main entry point of the application.
        /// Initializes all components and starts the HTTP server.
        /// </summary>
        /// <param name="args">Command line arguments (not used)</param>
        static void Main(string[] args)
        {
            // Устанавливаем кодировку для корректного отображения кириллицы в Windows консоли
            Console.OutputEncoding = Encoding.GetEncoding(866);
            Console.InputEncoding = Encoding.GetEncoding(866);
            
            Console.WriteLine($"=== Фейковый сервер SpeedTest v{AppVersion} ===");
            Console.WriteLine("Инициализация...");

            // Инициализация компонентов
            banManager = new BanManager("bans.ini");
            LoadSuspiciousUserAgents();
            InitializeFileWatcher();
            LoadRandomHeaders();
            CreateNewLogFile();

            // Инициализация службы ночного режима
            nightModeService = new NightModeService(banManager, Log);
            nightModeService.Start();

            // Инициализация обработчика запросов
            requestHandler = new RequestHandler(
                banManager,
                nightModeService,
                randomHeaders,
                headerLock,
                suspiciousUserAgents,
                Log,
                LogErrorToFile);

            // Запуск HTTP-слушателя
            listener = new HttpListener();
            listener.Prefixes.Add("http://+:5000/");
            listener.Start();
            Console.WriteLine("Сервер запущен на http://*:5000/");
            Log("Сервер запущен на порту 5000");

            serverCts = new CancellationTokenSource();

            // Обработчик Ctrl+C для корректного завершения работы
            Console.CancelKeyPress += (sender, e) =>
            {
                if (isShuttingDown)
                {
                    // Принудительное завершение при повторном нажатии
                    Environment.Exit(0);
                    return;
                }
                
                e.Cancel = true; // Отменяем стандартное завершение
                isShuttingDown = true;
                Console.WriteLine("\nПолучен сигнал завершения (Ctrl+C). Остановка сервера...");
                serverCts?.Cancel();
            };

            // Основной цикл
            MainLoop().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Основной цикл сервера, который принимает и обрабатывает входящие HTTP-запросы.
        /// Во время сна в ночном режиме слушатель останавливается для предотвращения обработки любых запросов.
        /// Обновляет заголовок окна с количеством подключений каждые 30 секунд.
        /// </summary>
        private static async Task MainLoop()
        {
            var lastTitleUpdate = DateTime.MinValue;
            bool listenerRunning = true;
            
            while (!serverCts.Token.IsCancellationRequested)
            {
                // Проверка ночного режима
                if (nightModeService.IsInSleepMode)
                {
                    // Остановка слушателя если он работает для обеспечения полной тишины
                    if (listenerRunning)
                    {
                        listener.Stop();
                        listenerRunning = false;
                        Console.WriteLine("[Ночной режим] Слушатель остановлен. Сервер спит.");
                    }
                    
                    await nightModeService.WaitForNightModeEndOrForceRun(serverCts);
                    
                    // Перезапуск слушателя после пробуждения
                    if (!listenerRunning && !serverCts.Token.IsCancellationRequested)
                    {
                        listener.Start();
                        listenerRunning = true;
                        Console.WriteLine("[Ночной режим] Слушатель запущен. Сервер проснулся.");
                    }
                }
                else
                {
                    // Обеспечение работы слушателя в дневном режиме
                    if (!listenerRunning)
                    {
                        listener.Start();
                        listenerRunning = true;
                    }
                }

                // Обновление заголовка окна с количеством подключений и забаненных IP каждые 30 секунд
                if ((DateTime.Now - lastTitleUpdate).TotalSeconds >= 30)
                {
                    int connections = banManager.GetConnectionsLast24Hours();
                    int bannedCount = banManager.GetBannedCount();
                    Console.Title = $"Фейковый сервер SpeedTest v{AppVersion} - Подключений (24ч): {connections} | Забанено адресов: {bannedCount}";
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
                    LogErrorToFile($"Ошибка основного цикла: {ex.Message}");
                }
            }

            listener.Stop();
            listener.Close();
            fileWatcher?.Dispose();
            nightModeService?.Stop();
            Console.WriteLine("Сервер остановлен.");
            Log("Сервер остановлен");
        }

        /// <summary>
        /// Загружает подозрительные пользовательские агенты из файла или использует значения по умолчанию.
        /// Отслеживается FileSystemWatcher для возможности горячей перезагрузки.
        /// </summary>
        private static void LoadSuspiciousUserAgents()
        {
            var filePath = "suspicious_agents.txt";
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Предупреждение: {filePath} не найден. Используются значения по умолчанию.");
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
                Console.WriteLine($"Загружено {suspiciousUserAgents.Count} подозрительных пользовательских агентов.");
            }
            catch (Exception ex)
            {
                LogErrorToFile($"Ошибка загрузки подозрительных агентов: {ex.Message}");
            }
        }

        /// <summary>
        /// Инициализирует FileSystemWatcher для мониторинга изменений в suspicious_agents.txt.
        /// Автоматически перезагружает список при изменении файла.
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
                // Защита от дребезга - ожидание 100 мс перед перезагрузкой
                Thread.Sleep(100);
                Console.WriteLine("Перезагрузка suspicious_agents.txt...");
                LoadSuspiciousUserAgents();
                Log("Файл suspicious_agents.txt перезагружен");
            };

            fileWatcher.EnableRaisingEvents = true;
            Console.WriteLine($"Мониторинг изменений файла {filePath}.");
        }

        /// <summary>
        /// Загружает случайные заголовки из файла для рандомизации заголовка ответа X-Powered-By.
        /// </summary>
        private static void LoadRandomHeaders()
        {
            var filePath = "random_headers.txt";
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Предупреждение: {filePath} не найден.");
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
                Console.WriteLine($"Загружено {randomHeaders.Count} случайных заголовков.");
            }
            catch (Exception ex)
            {
                LogErrorToFile($"Ошибка загрузки случайных заголовков: {ex.Message}");
            }
        }

        /// <summary>
        /// Создаёт новый лог-файл с меткой времени в формате dd-MM-yyyy.
        /// Вызывается при запуске сервера для начала сеанса логирования.
        /// </summary>
        private static void CreateNewLogFile()
        {
            var timestamp = DateTime.Now.ToString("dd-MM-yyyy_HH-mm-ss");
            currentLogFile = $"server_log_{timestamp}.txt";
            
            // Создание пустого лог-файла с заголовком
            File.WriteAllText(currentLogFile, $"=== Журнал запущен {DateTime.Now:dd-MM-yyyy HH:mm:ss} ===\r\n", Encoding.UTF8);
        }

        /// <summary>
        /// Записывает информационное сообщение в консоль и текущий лог-файл.
        /// Потокобезопасно с использованием блокировки logLock.
        /// </summary>
        /// <param name="message">Сообщение для записи в лог</param>
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
                    Console.WriteLine($"Ошибка записи в журнал: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Записывает сообщение об ошибке в консоль и текущий лог-файл.
        /// Потокобезопасно с использованием блокировки logLock.
        /// </summary>
        /// <param name="message">Сообщение об ошибке для записи в лог</param>
        private static void LogErrorToFile(string message)
        {
            var timestamp = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
            var logEntry = $"[{timestamp}] ОШИБКА: {message}";
            
            Console.WriteLine(logEntry);
            
            lock (logLock)
            {
                try
                {
                    File.AppendAllText(currentLogFile, logEntry + "\r\n", Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка записи ошибки в журнал: {ex.Message}");
                }
            }
        }
    }
}
