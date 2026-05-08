using System;
using System.Threading;
using System.Threading.Tasks;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Управляет функциональностью ночного режима сервера.
    /// Контролирует циклы сна/пробуждения на основе настроенных часов (по умолчанию 01:00-06:00).
    /// Обеспечивает потокобезопасное управление состоянием и возможность принудительного запуска.
    /// </summary>
    public class NightModeService
    {
        private readonly object _nightModeLock = new object();
        private volatile bool _isInSleepMode = false;
        private volatile bool _isForceRunRequested = false;
        private volatile bool _isNightModeEnabled = true;
        private int _nightStartHour = 1; // 01:00
        private int _nightEndHour = 6;   // 06:00
        private readonly BanManager _banManager;
        private readonly Action<string> _logAction;
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// Возвращает значение, указывающее, находится ли сервер в настоящее время в спящем режиме.
        /// </summary>
        public bool IsInSleepMode
        {
            get
            {
                lock (_nightModeLock)
                {
                    return _isInSleepMode && !_isForceRunRequested;
                }
            }
        }

        /// <summary>
        /// Возвращает значение, указывающее, включён ли ночной режим.
        /// </summary>
        public bool IsNightModeEnabled => _isNightModeEnabled;

        /// <summary>
        /// Инициализирует новый экземпляр класса NightModeService.
        /// </summary>
        /// <param name="banManager">Экземпляр BanManager для операций очистки</param>
        /// <param name="logAction">Действие для ведения журнала сообщений</param>
        public NightModeService(BanManager banManager, Action<string> logAction)
        {
            _banManager = banManager;
            _logAction = logAction;
        }

        /// <summary>
        /// Запускает фоновую задачу мониторинга ночного режима.
        /// Непрерывно проверяет время и соответствующим образом обновляет состояние сна.
        /// </summary>
        public void Start()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Task.Run(() => CheckNightModeAsync(_cancellationTokenSource.Token));
        }

        /// <summary>
        /// Останавливает фоновую задачу мониторинга ночного режима.
        /// </summary>
        public void Stop()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Проверяет, попадает ли текущее время в часы ночного режима.
        /// Обрабатывает как диапазоны одного дня (например, 1-6), так и диапазоны через полночь.
        /// </summary>
        /// <returns>True, если текущее время находится в пределах часов ночного режима, иначе false</returns>
        private bool IsNightTime()
        {
            var now = DateTime.Now.Hour;
            if (_nightStartHour < _nightEndHour)
            {
                return now >= _nightStartHour && now < _nightEndHour;
            }
            else
            {
                return now >= _nightStartHour || now < _nightEndHour;
            }
        }

        /// <summary>
        /// Background task that continuously monitors time and updates sleep mode state.
        /// Runs every second and triggers cleanup of old tracking data.
        /// </summary>
        /// <param name="token">Cancellation token for stopping the task</param>
        private async Task CheckNightModeAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);

                    if (!_isNightModeEnabled)
                        continue;

                    bool isNight = IsNightTime();

                    lock (_nightModeLock)
                    {
                        if (isNight && !_isInSleepMode)
                        {
                            _isInSleepMode = true;
                            _isForceRunRequested = false;
                            Console.WriteLine("[Ночной режим] Переход в спящий режим.");
                            _logAction?.Invoke("Ночной режим запущен");
                        }
                        else if (!isNight && _isInSleepMode)
                        {
                            _isInSleepMode = false;
                            _isForceRunRequested = false;
                            Console.WriteLine("[Ночной режим] Выход из спящего режима.");
                            _logAction?.Invoke("Ночной режим завершён");
                        }
                    }

                    // Cleanup old tracking every minute
                    _banManager.CleanupOldTracking(60);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logAction?.Invoke($"Ошибка проверки ночного режима: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Waits for night mode to end or for a force-run request.
        /// Monitors console input for 'Y' key to force run during sleep mode.
        /// </summary>
        /// <param name="serverCts">Server cancellation token to detect shutdown</param>
        /// <returns>Task that completes when night mode ends or force run is requested</returns>
        public async Task WaitForNightModeEndOrForceRun(CancellationTokenSource serverCts)
        {
            Console.WriteLine("[Ночной режим] Сервер спит. Нажмите 'Y' для принудительного запуска.");

            while (IsInSleepMode)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Y)
                    {
                        lock (_nightModeLock)
                        {
                            _isForceRunRequested = true;
                        }
                        Console.WriteLine("[Ночной режим] Запрошен принудительный запуск!");
                        break;
                    }
                }
                await Task.Delay(1000, serverCts.Token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Requests a force run during night mode sleep.
        /// Allows server to operate normally even during night hours.
        /// </summary>
        public void RequestForceRun()
        {
            lock (_nightModeLock)
            {
                _isForceRunRequested = true;
            }
        }

        /// <summary>
        /// Sets the night mode start hour.
        /// </summary>
        /// <param name="hour">Hour (0-23) when night mode starts</param>
        public void SetNightStartHour(int hour)
        {
            _nightStartHour = hour;
        }

        /// <summary>
        /// Sets the night mode end hour.
        /// </summary>
        /// <param name="hour">Hour (0-23) when night mode ends</param>
        public void SetNightEndHour(int hour)
        {
            _nightEndHour = hour;
        }

        /// <summary>
        /// Enables or disables night mode functionality.
        /// </summary>
        /// <param name="enabled">True to enable night mode, false to disable</param>
        public void SetNightModeEnabled(bool enabled)
        {
            _isNightModeEnabled = enabled;
        }
    }
}
