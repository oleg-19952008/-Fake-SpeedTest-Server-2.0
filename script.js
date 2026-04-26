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
    
    window.location.href = '/download/' + sizeMB;
}

function setAndDownload(sizeMB) {
    var sizeInput = document.getElementById('fileSize');
    sizeInput.value = sizeMB;
    startDownload();
}

// Get browser info using Client Hints
if (navigator.userAgentData) {
    navigator.userAgentData.getHighEntropyValues(['platform', 'platformVersion', 'architecture', 'model', 'uaFullVersion']).then(function(info) {
        document.getElementById('infoText').innerHTML = 
            '<strong>Платформа:</strong> ' + (info.platform || navigator.platform) + '<br>' +
            '<strong>Браузер:</strong> ' + navigator.userAgentData.brand + ' ' + (info.uaFullVersion || '') + '<br>' +
            '<strong>Архитектура:</strong> ' + (info.architecture || 'N/A') + '<br>' +
            '<strong>Версия ОС:</strong> ' + (info.platformVersion || 'N/A');
    });
} else {
    document.getElementById('infoText').innerHTML = 
        '<strong>User-Agent:</strong> ' + navigator.userAgent + '<br>' +
        '<strong>Платформа:</strong> ' + navigator.platform;
}
