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
    /// Главный входной пункт приложения Fake SpeedTest Server.
    /// Организует все компоненты сервера, включая управление банами, ночной режим,
    /// обработку запросов, ведение журнала и загрузку конфигурации.
    /// </summary>
    class Program
    {
        // Глобальная константа версии
        private const string AppVersion = "2.13.1";
        
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

        // Пользовательский вывод в консоль и файл
        private static ConsoleAndFileWriter consoleAndFileWriter;
        private static string currentLogFile;

        /// <summary>
        /// Главная точка входа приложения.
        /// Инициализирует все компоненты и запускает HTTP-сервер.
        /// </summary>
        /// <param name="args">Аргументы командной строки (не используются)</param>
        static void Main(string[] args)
        {
            Console.Clear();
            // Создаем файл логов до установки переопределения Console.Out
            CreateNewLogFile();

            // Устанавливаем кодировку для корректного отображения кириллицы в Windows консоли
            Console.OutputEncoding = Encoding.GetEncoding(866);
            Console.InputEncoding = Encoding.GetEncoding(866);
            
            // Переопределяем вывод консоли для записи одновременно в консоль и файл логов
            consoleAndFileWriter = new ConsoleAndFileWriter(currentLogFile);
            Console.SetOut(consoleAndFileWriter);

            Console.WriteLine($"=== Фейковый сервер SpeedTest v{AppVersion} ===");
            Console.WriteLine("Инициализация...");

            // Инициализация компонентов
            banManager = new BanManager("bans.ini");
            LoadSuspiciousUserAgents();
            InitializeFileWatcher();
            LoadRandomHeaders();

            // Инициализация службы ночного режима
            nightModeService = new NightModeService();

            // Инициализация обработчика запросов
            requestHandler = new RequestHandler(
                banManager,
                nightModeService,
                randomHeaders,
                headerLock,
                suspiciousUserAgents);

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
            MainLoop();
        }

        /// <summary>
        /// Основной цикл сервера, который принимает и обрабатывает входящие HTTP-запросы.
        /// Работает в одном потоке, проверяя нажатие кнопки и время.
        /// </summary>
        private static void MainLoop()
        {
            var lastTitleUpdate = DateTime.MinValue;
            
            while (!serverCts.Token.IsCancellationRequested)
            {
                // Проверка ночного режима и кнопки принудительного запуска
                if (nightModeService.IsNightModeEnabled && nightModeService.IsNightTime() && !nightModeService.IsForceRunRequested)
                {
                    // Ночной режим активен - проверяем кнопку 'Y' для принудительного запуска
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Y)
                        {
                            nightModeService.RequestForceRun();
                            Console.WriteLine("[Ночной режим] Запрошен принудительный запуск!");
                        }
                    }
                    
                    // Небольшая пауза чтобы не нагружать CPU
                    Thread.Sleep(100);
                    continue;
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
                    var context = listener.GetContext();
                    _ = requestHandler.HandleRequestAsync(context);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка основного цикла: {ex.Message}");
                }
            }

            listener.Stop();
            listener.Close();
            fileWatcher?.Dispose();
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
                Console.WriteLine($"Ошибка загрузки подозрительных агентов: {ex.Message}");
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
                Console.WriteLine($"Ошибка загрузки случайных заголовков: {ex.Message}");
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
        /// Использует переопределенный Console.WriteLine, поэтому пишет только уникальное сообщение без дублирования временной метки.
        /// </summary>
        /// <param name="message">Сообщение для записи в лог</param>
        private static void Log(string message)
        {
            Console.WriteLine(message);
        }
    }
}
