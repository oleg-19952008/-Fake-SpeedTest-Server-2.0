using System;
using System.Threading;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Управляет состоянием ночного режима сервера.
    /// Хранит настройки времени и состояние сна/принудительного запуска.
    /// Не содержит собственных потоков - управление осуществляется из главного цикла.
    /// </summary>
    public class NightModeService
    {
        private readonly object _lock = new object();
        private bool _isInSleepMode = false;
        private bool _isForceRunRequested = false;
        private int _nightStartHour = 1; // 01:00
        private int _nightEndHour = 6;   // 06:00
        private bool _isNightModeEnabled = true;

        /// <summary>
        /// Возвращает значение, указывающее, находится ли сервер в настоящее время в спящем режиме.
        /// </summary>
        public bool IsInSleepMode
        {
            get
            {
                lock (_lock)
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
        /// Проверяет, попадает ли текущее время в часы ночного режима.
        /// </summary>
        /// <returns>True, если текущее время находится в пределах часов ночного режима, иначе false</returns>
        public bool IsNightTime()
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
        /// Устанавливает час начала ночного режима.
        /// </summary>
        public void SetNightStartHour(int hour) => _nightStartHour = hour;

        /// <summary>
        /// Устанавливает час окончания ночного режима.
        /// </summary>
        public void SetNightEndHour(int hour) => _nightEndHour = hour;

        /// <summary>
        /// Включает или отключает функциональность ночного режима.
        /// </summary>
        public void SetNightModeEnabled(bool enabled)
        {
            lock (_lock)
            {
                _isNightModeEnabled = enabled;
            }
        }

        /// <summary>
        /// Запрашивает принудительный запуск во время спящего режима.
        /// </summary>
        public void RequestForceRun()
        {
            lock (_lock)
            {
                _isForceRunRequested = true;
            }
        }

        /// <summary>
        /// Сбрасывает состояние ночного режима (вызывается при выходе из ночного режима).
        /// </summary>
        public void ResetSleepState()
        {
            lock (_lock)
            {
                _isInSleepMode = false;
                _isForceRunRequested = false;
            }
        }

        /// <summary>
        /// Устанавливает состояние сна (вызывается при входе в ночной режим).
        /// </summary>
        public void EnterSleepMode()
        {
            lock (_lock)
            {
                _isInSleepMode = true;
                _isForceRunRequested = false;
            }
        }
    }
}
