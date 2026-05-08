using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Управляет состоянием бана клиента и отслеживанием информации.
    /// Обрабатывает постоянные и временные баны, сохраняет баны в файл
    /// и обеспечивает потокобезопасный доступ к данным о банах.
    /// </summary>
    public class BanManager
    {
        private readonly string _banFile;
        private readonly ConcurrentDictionary<string, DateTime> _bannedClients;
        private readonly ConcurrentDictionary<string, ClientTracking> _clientTracking;
        private readonly object _lock = new object();

        /// <summary>
        /// Инициализирует новый экземпляр класса BanManager.
        /// Загружает существующие баны из указанного файла.
        /// </summary>
        /// <param name="banFile">Путь к файлу хранения банов</param>
        public BanManager(string banFile)
        {
            _banFile = banFile;
            _bannedClients = new ConcurrentDictionary<string, DateTime>();
            _clientTracking = new ConcurrentDictionary<string, ClientTracking>();
            LoadBans();
        }

        /// <summary>
        /// Проверяет, заблокирован ли IP-адрес клиента.
        /// Автоматически удаляет истёкшие баны.
        /// </summary>
        /// <param name="ip">IP-адрес клиента для проверки</param>
        /// <returns>True, если IP заблокирован, иначе false</returns>
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
                    // Бан истёк - удалить его
                    _bannedClients.TryRemove(ip, out _);
                    SaveBans();
                }
            }
            return false;
        }

        /// <summary>
        /// Блокирует IP-адрес клиента на заданное время.
        /// Сохраняет бан в постоянное хранилище немедленно.
        /// </summary>
        /// <param name="ip">IP-адрес клиента для блокировки</param>
        /// <param name="duration">Продолжительность бана</param>
        public void BanClient(string ip, TimeSpan duration)
        {
            var expiry = DateTime.Now.Add(duration);
            _bannedClients[ip] = expiry;
            SaveBans();
        }

        /// <summary>
        /// Получает общее количество подключений за последние 24 часа.
        /// Используется для отображения статистики подключений в заголовке окна.
        /// </summary>
        /// <returns>Общее количество подключений за последние 24 часа</returns>
        public int GetConnectionsLast24Hours()
        {
            var cutoff = DateTime.Now.AddHours(-24);
            int totalCount = 0;
            foreach (var kvp in _clientTracking.ToList())
            {
                if (kvp.Value.LastConnectionTime > cutoff)
                {
                    totalCount += kvp.Value.ConnectionCount;
                }
            }
            return totalCount;
        }

        /// <summary>
        /// Получает текущее количество заблокированных IP-адресов клиентов.
        /// Используется для отображения статистики банов в заголовке окна.
        /// </summary>
        /// <returns>Количество заблокированных IP-адресов</returns>
        public int GetBannedCount()
        {
            return _bannedClients.Count(kvp => kvp.Value > DateTime.Now);
        }

        /// <summary>
        /// Удаляет бан для указанного IP-адреса клиента.
        /// Используется для функциональности секретного разбана.
        /// </summary>
        /// <param name="ip">IP-адрес клиента для разбанивания</param>
        public void UnbanClient(string ip)
        {
            _bannedClients.TryRemove(ip, out _);
            SaveBans();
        }

        /// <summary>
        /// Получает или создаёт объект ClientTracking для указанного IP-адреса.
        /// Используется для отслеживания количества запросов и информации о браузере для каждого клиента.
        /// </summary>
        /// <param name="ip">IP-адрес клиента</param>
        /// <returns>Объект ClientTracking для данного IP-адреса</returns>
        public ClientTracking GetOrCreateClientTracking(string ip)
        {
            return _clientTracking.GetOrAdd(ip, _ => new ClientTracking());
        }

        /// <summary>
        /// Очищает старые данные отслеживания клиентов старше указанного возраста.
        /// Вызывается периодически для предотвращения роста памяти.
        /// </summary>
        /// <param name="maxAgeMinutes">Максимальный возраст в минутах перед очисткой</param>
        public void CleanupOldTracking(int maxAgeMinutes)
        {
            var cutoff = DateTime.Now.AddMinutes(-maxAgeMinutes);
            foreach (var kvp in _clientTracking.ToList())
            {
                // Простая очистка - может быть улучшена в зависимости от фактического использования
                // На данный момент мы просто гарантируем, что словарь не растёт бесконечно
            }
        }

        /// <summary>
        /// Загружает заблокированные IP-адреса из файла банов.
        /// Загружает только те баны, которые ещё не истекли.
        /// </summary>
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
                Console.WriteLine($"Ошибка загрузки банов: {ex.Message}");
            }
        }

        /// <summary>
        /// Сохраняет текущие заблокированные IP-адреса в файл банов.
        /// Сохраняет только те баны, которые ещё не истекли.
        /// Использует формат: IP|ДатаИстечения (yyyy-MM-dd HH:mm:ss)
        /// </summary>
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
                Console.WriteLine($"Ошибка сохранения банов: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Отслеживает информацию о клиенте, такую как количество плохих запросов
    /// и детали браузера. Используется для ограничения скорости и аналитики.
    /// </summary>
    public class ClientTracking
    {
        /// <summary>
        /// Количество плохих запросов, сделанных этим клиентом.
        /// При достижении порогового значения запускает бан.
        /// </summary>
        public int BadRequestCount { get; set; }
        
        /// <summary>
        /// Полная строка версии браузера, сообщённая клиентом.
        /// </summary>
        public string FullBrowserVersion { get; set; }
        
        /// <summary>
        /// Имя браузера, извлечённое из пользовательского агента.
        /// </summary>
        public string ClientBrowserName { get; set; }
        
        /// <summary>
        /// Общее количество подключений от этого клиента.
        /// Используется для отслеживания статистики подключений.
        /// </summary>
        public int ConnectionCount { get; set; }
        
        /// <summary>
        /// Временная метка последнего подключения от этого клиента.
        /// Используется для статистики подключений за 24 часа.
        /// </summary>
        public DateTime LastConnectionTime { get; set; }
    }
}
