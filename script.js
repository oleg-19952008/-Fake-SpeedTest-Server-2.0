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
