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
    
    // Log the download attempt to console for debugging
    console.log('Starting download: ' + sizeMB + ' MB');
    
    window.location.href = '/download/' + sizeMB;
}

function setAndDownload(sizeMB) {
    var sizeInput = document.getElementById('fileSize');
    sizeInput.value = sizeMB;
    startDownload();
}

// Get browser info using Client Hints and fetch IP from server
function loadBrowserInfo() {
    // First get basic browser info
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
    // Fetch IP address from server
    fetch('/updateBrowserInfo')
        .then(function(response) { return response.json(); })
        .then(function(data) {
            var ipInfo = data.ipAddress ? '<br><strong>IP адрес:</strong> ' + data.ipAddress : '';
            document.getElementById('infoText').innerHTML = basicInfo + ipInfo;
        })
        .catch(function(error) {
            console.error('Error fetching IP:', error);
            // Still show basic info even if IP fetch fails
        });
}

// Load browser info on page load
loadBrowserInfo();

// Theme toggle functionality with auto-detect system preference
(function() {
    var themeToggle = document.getElementById('themeToggle');
    var body = document.body;
    
    // Function to update button icon based on current theme
    function updateThemeIcon(isDark) {
        themeToggle.textContent = isDark ? '☀️' : '🌙';
        themeToggle.setAttribute('title', isDark ? 'Переключить на светлую тему' : 'Переключить на тёмную тему');
    }
    
    // Function to apply theme
    function applyTheme(theme) {
        body.classList.remove('light-theme', 'dark-theme');
        body.classList.add(theme + '-theme');
        localStorage.setItem('theme', theme);
        updateThemeIcon(theme === 'dark');
    }
    
    // Check for saved theme or auto-detect system preference
    var savedTheme = localStorage.getItem('theme');
    
    if (savedTheme) {
        // Use saved theme
        applyTheme(savedTheme);
    } else {
        // Auto-detect system theme preference
        var prefersDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
        applyTheme(prefersDark ? 'dark' : 'light');
    }
    
    // Listen for system theme changes
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function(e) {
            // Only auto-switch if user hasn't manually set a preference
            if (!localStorage.getItem('theme')) {
                applyTheme(e.matches ? 'dark' : 'light');
            }
        });
    }
    
    // Toggle theme on button click
    themeToggle.addEventListener('click', function() {
        var isCurrentlyDark = body.classList.contains('dark-theme');
        applyTheme(isCurrentlyDark ? 'light' : 'dark');
    });
})();

// Measure ping using 3 sequential fetch requests with 10ms delay between each and calculate jitter
async function measurePing() {
    var measurements = [];
    
    // Perform 3 sequential requests with 10ms delay between each
    for (var i = 0; i < 3; i++) {
        var startTime = performance.now();
        try {
            await fetch('/ping', { method: 'GET' });
            var endTime = performance.now();
            measurements.push(endTime - startTime);
        } catch (error) {
            console.error('Ping request failed:', error);
        }
        
        // Add 10ms delay between requests (except after the last one)
        if (i < 2) {
            await new Promise(function(resolve) { setTimeout(resolve, 10); });
        }
    }
    
    // Return null if we have no measurements
    if (measurements.length === 0) {
        return null;
    }
    
    // Sort measurements to find median
    measurements.sort(function(a, b) { return a - b; });
    
    // Calculate median
    var mid = Math.floor(measurements.length / 2);
    var median;
    if (measurements.length % 2 === 0) {
        median = (measurements[mid - 1] + measurements[mid]) / 2;
    } else {
        median = measurements[mid];
    }
    
    // Calculate jitter (standard deviation of measurements)
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

// Start ping measurement and display result with anti-spam protection (2 second cooldown)
var lastPingMeasurementTime = 0;
var isMeasuringPing = false;

async function startPingMeasurement() {
    var resultDiv = document.getElementById('pingResult');
    
    // Check if already measuring
    if (isMeasuringPing) {
        return;
    }
    
    // Check cooldown (2 seconds)
    var currentTime = Date.now();
    var timeSinceLastMeasurement = currentTime - lastPingMeasurementTime;
    if (timeSinceLastMeasurement < 2000 && lastPingMeasurementTime !== 0) {
        resultDiv.style.display = 'block';
        resultDiv.innerHTML = 'Подождите ' + Math.ceil((2000 - timeSinceLastMeasurement) / 1000) + ' сек...';
        return;
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
