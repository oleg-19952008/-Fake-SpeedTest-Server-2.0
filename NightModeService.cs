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
        /// Фоновая задача, которая непрерывно отслеживает время и обновляет состояние спящего режима.
        /// Запускается каждую секунду и запускает очистку старых данных отслеживания.
        /// </summary>
        /// <param name="token">Токен отмены для остановки задачи</param>
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
        /// Ожидает завершения ночного режима или запроса принудительного запуска.
        /// Отслеживает ввод консоли для клавиши 'Y' для принудительного запуска во время спящего режима.
        /// </summary>
        /// <param name="serverCts">Токен отмены сервера для обнаружения завершения работы</param>
        /// <returns>Задача, которая завершается, когда ночной режим заканчивается или запрошен принудительный запуск</returns>
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
        /// Запрашивает принудительный запуск во время спящего режима ночного режима.
        /// Позволяет серверу работать нормально даже в ночные часы.
        /// </summary>
        public void RequestForceRun()
        {
            lock (_nightModeLock)
            {
                _isForceRunRequested = true;
            }
        }

        /// <summary>
        /// Устанавливает час начала ночного режима.
        /// </summary>
        /// <param name="hour">Час (0-23), когда начинается ночной режим</param>
        public void SetNightStartHour(int hour)
        {
            _nightStartHour = hour;
        }

        /// <summary>
        /// Устанавливает час окончания ночного режима.
        /// </summary>
        /// <param name="hour">Час (0-23), когда заканчивается ночной режим</param>
        public void SetNightEndHour(int hour)
        {
            _nightEndHour = hour;
        }

        /// <summary>
        /// Включает или отключает функциональность ночного режима.
        /// </summary>
        /// <param name="enabled">True для включения ночного режима, false для отключения</param>
        public void SetNightModeEnabled(bool enabled)
        {
            _isNightModeEnabled = enabled;
        }
    }
}
