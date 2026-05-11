using System;
using System.Threading;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Управляет функциональностью ночного режима сервера.
    /// Предоставляет методы для проверки времени и запроса принудительного запуска.
    /// </summary>
    public class NightModeService
    {
        private volatile bool _isForceRunRequested = false;
        private volatile bool _isNightModeEnabled = true;
        private int _nightStartHour = 1; // 01:00
        private int _nightEndHour = 6;   // 06:00

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
        /// Проверяет, был ли запрошен принудительный запуск.
        /// </summary>
        public bool IsForceRunRequested => _isForceRunRequested;

        /// <summary>
        /// Запрашивает принудительный запуск во время ночного режима.
        /// Позволяет серверу работать нормально даже в ночные часы.
        /// </summary>
        public void RequestForceRun()
        {
            _isForceRunRequested = true;
        }

        /// <summary>
        /// Сбрасывает флаг принудительного запуска.
        /// </summary>
        public void ResetForceRun()
        {
            _isForceRunRequested = false;
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
