using System;
using System.Threading;
using System.Threading.Tasks;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Manages night mode functionality for the server.
    /// Controls sleep/wake cycles based on configured hours (01:00-06:00 by default).
    /// Provides thread-safe state management and force-run capability.
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
        /// Gets a value indicating whether the server is currently in sleep mode.
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
        /// Gets a value indicating whether night mode is enabled.
        /// </summary>
        public bool IsNightModeEnabled => _isNightModeEnabled;

        /// <summary>
        /// Initializes a new instance of the NightModeService class.
        /// </summary>
        /// <param name="banManager">BanManager instance for cleanup operations</param>
        /// <param name="logAction">Action to use for logging messages</param>
        public NightModeService(BanManager banManager, Action<string> logAction)
        {
            _banManager = banManager;
            _logAction = logAction;
        }

        /// <summary>
        /// Starts the night mode monitoring background task.
        /// Continuously checks time and updates sleep state accordingly.
        /// </summary>
        public void Start()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Task.Run(() => CheckNightModeAsync(_cancellationTokenSource.Token));
        }

        /// <summary>
        /// Stops the night mode monitoring background task.
        /// </summary>
        public void Stop()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Checks if the current time falls within night mode hours.
        /// Handles both same-day ranges (e.g., 1-6) and cross-midnight ranges.
        /// </summary>
        /// <returns>True if current time is within night mode hours, false otherwise</returns>
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
                            Console.WriteLine("[Night Mode] Entering sleep mode.");
                            _logAction?.Invoke("Night mode started");
                        }
                        else if (!isNight && _isInSleepMode)
                        {
                            _isInSleepMode = false;
                            _isForceRunRequested = false;
                            Console.WriteLine("[Night Mode] Exiting sleep mode.");
                            _logAction?.Invoke("Night mode ended");
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
                    _logAction?.Invoke($"Night mode check error: {ex.Message}");
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
            Console.WriteLine("[Night Mode] Server is sleeping. Press 'Y' to force run.");

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
                        Console.WriteLine("[Night Mode] Force run requested!");
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
