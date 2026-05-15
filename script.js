function startDownload() {
    var sizeInput = document.getElementById('fileSize');
    var sizeMB = parseInt(sizeInput.value);
    
    if (sizeMB < 1 || sizeMB > 10240) {
        alert('Размер файла должен быть от 1 до 10240 МБ');
        return;
    }
    
    var resultDiv = document.getElementById('result');
    resultDiv.style.display = 'block';
    resultDiv.innerHTML = 'Скачивание файла ' + sizeMB + ' МБ...';
    
    // Журналирование попытки скачивания в консоль для отладки
    console.log('Starting download: ' + sizeMB + ' MB');
    
    window.location.href = '/download/' + sizeMB;
}

function setAndDownload(sizeMB) {
    var sizeInput = document.getElementById('fileSize');
    sizeInput.value = sizeMB;
    startDownload();
}

// Получение информации о браузере с помощью Client Hints и запрос IP-адреса с сервера
function loadBrowserInfo() {
    // Сначала получаем базовую информацию о браузере
    var basicInfo = '';
    if (navigator.userAgentData) {
        navigator.userAgentData.getHighEntropyValues(['platform', 'platformVersion', 'architecture', 'model', 'uaFullVersion']).then(function(info) {
            basicInfo = 
                '<strong>Платформа:</strong> ' + (info.platform || navigator.platform) + '<br>' +
                '<strong>Браузер:</strong> ' + navigator.userAgentData.brand + ' ' + (info.uaFullVersion || '') + '<br>' +
                '<strong>Архитектура:</strong> ' + (info.architecture || 'N/A') + '<br>' +
                '<strong>Версия ОС:</strong> ' + (info.platformVersion || 'N/A');
            updateBrowserInfoDisplay(basicInfo);
        });
    } else {
        basicInfo = 
            '<strong>User-Agent:</strong> ' + navigator.userAgent + '<br>' +
            '<strong>Платформа:</strong> ' + navigator.platform;
        updateBrowserInfoDisplay(basicInfo);
    }
}

function updateBrowserInfoDisplay(basicInfo) {
    // Запрос IP-адреса с сервера
    fetch('/updateBrowserInfo')
        .then(function(response) { return response.json(); })
        .then(function(data) {
            var ipInfo = data.ipAddress ? '<br><strong>IP адрес:</strong> ' + data.ipAddress : '';
            document.getElementById('infoText').innerHTML = basicInfo + ipInfo;
        })
        .catch(function(error) {
            console.error('Error fetching IP:', error);
            // Всё равно показываем базовую информацию, даже если запрос IP не удался
        });
}

// Загрузка информации о браузере при загрузке страницы
loadBrowserInfo();

// Функциональность переключения темы с автоматическим определением системной предпочтительной темы
(function() {
    var themeToggle = document.getElementById('themeToggle');
    var body = document.body;
    
    // Функция обновления иконки кнопки в зависимости от текущей темы
    function updateThemeIcon(isDark) {
        themeToggle.textContent = isDark ? '☀️' : '🌙';
        themeToggle.setAttribute('title', isDark ? 'Переключить на светлую тему' : 'Переключить на тёмную тему');
    }
    
    // Функция применения темы
    function applyTheme(theme) {
        body.classList.remove('light-theme', 'dark-theme');
        body.classList.add(theme + '-theme');
        localStorage.setItem('theme', theme);
        updateThemeIcon(theme === 'dark');
    }
    
    // Проверка сохранённой темы или автоматическое определение системной предпочтительной темы
    var savedTheme = localStorage.getItem('theme');
    
    if (savedTheme) {
        // Использование сохранённой темы
        applyTheme(savedTheme);
    } else {
        // Автоматическое определение системной предпочтительной темы
        var prefersDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
        applyTheme(prefersDark ? 'dark' : 'light');
    }
    
    // Прослушивание изменений системной темы
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function(e) {
            // Автоматическое переключение только если пользователь не установил предпочтения вручную
            if (!localStorage.getItem('theme')) {
                applyTheme(e.matches ? 'dark' : 'light');
            }
        });
    }
    
    // Переключение темы по клику на кнопку
    themeToggle.addEventListener('click', function() {
        var isCurrentlyDark = body.classList.contains('dark-theme');
        applyTheme(isCurrentlyDark ? 'light' : 'dark');
    });
})();

// Измерение пинга с помощью 3 последовательных fetch-запросов с задержкой 10 мс между каждым и расчёт джиттера
async function measurePing() {
    var measurements = [];
    
    // Выполнение 3 последовательных запросов с задержкой 10 мс между каждым
    for (var i = 0; i < 3; i++) {
        var startTime = performance.now();
        try {
            await fetch('/ping', { method: 'GET' });
            var endTime = performance.now();
            measurements.push(endTime - startTime);
        } catch (error) {
            console.error('Ping request failed:', error);
        }
        
        // Добавление задержки 10 мс между запросами (кроме последнего)
        if (i < 2) {
            await new Promise(function(resolve) { setTimeout(resolve, 10); });
        }
    }
    
    // Возврат null если нет измерений
    if (measurements.length === 0) {
        return null;
    }
    
    // Сортировка измерений для нахождения медианы
    measurements.sort(function(a, b) { return a - b; });
    
    // Вычисление медианы
    var mid = Math.floor(measurements.length / 2);
    var median;
    if (measurements.length % 2 === 0) {
        median = (measurements[mid - 1] + measurements[mid]) / 2;
    } else {
        median = measurements[mid];
    }
    
    // Вычисление джиттера (стандартное отклонение измерений)
    var sum = 0;
    for (var j = 0; j < measurements.length; j++) {
        sum += measurements[j];
    }
    var avg = sum / measurements.length;
    
    var varianceSum = 0;
    for (var k = 0; k < measurements.length; k++) {
        varianceSum += Math.pow(measurements[k] - avg, 2);
    }
    var jitter = Math.sqrt(varianceSum / measurements.length);
    
    return {
        ping: median,
        jitter: jitter,
        min: measurements[0],
        max: measurements[measurements.length - 1],
        avg: avg
    };
}

// Запуск измерения пинга и отображение результата с защитой от спама (задержка 2 секунды)
var lastPingMeasurementTime = 0;
var isMeasuringPing = false;
var pingWaitTimer = null;

async function startPingMeasurement() {
    var resultDiv = document.getElementById('pingResult');
    
    // Проверка уже идёт ли измерение
    if (isMeasuringPing) {
        return;
    }
    
    // Проверка задержки (2 секунды)
    var currentTime = Date.now();
    var timeSinceLastMeasurement = currentTime - lastPingMeasurementTime;
    if (timeSinceLastMeasurement < 2000 && lastPingMeasurementTime !== 0) {
        resultDiv.style.display = 'block';
        
        // Очистка предыдущего таймера если есть
        if (pingWaitTimer) {
            clearInterval(pingWaitTimer);
        }
        
        // Функция обновления сообщения ожидания
        function updateWaitMessage() {
            var currentTime = Date.now();
            var timeSinceLastMeasurement = currentTime - lastPingMeasurementTime;
            var remainingTime = 2000 - timeSinceLastMeasurement;
            
            if (remainingTime <= 0) {
                resultDiv.innerHTML = 'Измерить пинг';
                if (pingWaitTimer) {
                    clearInterval(pingWaitTimer);
                    pingWaitTimer = null;
                }
            } else {
                resultDiv.innerHTML = 'Подождите ' + Math.ceil(remainingTime / 1000) + ' сек...';
            }
        }
        
        // Первоначальное отображение
        updateWaitMessage();
        
        // Обновление сообщения каждую секунду
        pingWaitTimer = setInterval(updateWaitMessage, 500);
        return;
    }
    
    // Очистка таймера ожидания если он был
    if (pingWaitTimer) {
        clearInterval(pingWaitTimer);
        pingWaitTimer = null;
    }
    
    isMeasuringPing = true;
    lastPingMeasurementTime = currentTime;
    
    resultDiv.style.display = 'block';
    resultDiv.innerHTML = 'Измерение пинга...';
    
    var result = await measurePing();
    
    isMeasuringPing = false;
    
    if (result !== null) {
        resultDiv.innerHTML = '<strong>Результаты измерения пинга:</strong><br>' +
                              'Пинг (медиана): ' + result.ping.toFixed(2) + ' мс<br>' +
                              'Джиттер: ' + result.jitter.toFixed(2) + ' мс<br>' +
                              'Средний: ' + result.avg.toFixed(2) + ' мс<br>' +
                              'Мин: ' + result.min.toFixed(2) + ' мс | Макс: ' + result.max.toFixed(2) + ' мс';
    } else {
        resultDiv.innerHTML = 'Ошибка измерения пинга';
    }
}
