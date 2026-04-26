function startTest(sizeMB) {
    var startTime = performance.now();
    var link = event.target;
    var resultDiv = document.getElementById('result');
    resultDiv.style.display = 'block';
    resultDiv.innerHTML = 'Тестирование...';
    
    // Simulate test completion after download starts
    setTimeout(function() {
        var endTime = performance.now();
        var duration = (endTime - startTime) / 1000;
        var speedMbps = (sizeMB * 8) / duration / 1000000;
        resultDiv.innerHTML = '<strong>Результат:</strong> Файл ' + sizeMB + ' МБ<br>' +
                              '<strong>Время:</strong> ' + duration.toFixed(2) + ' сек<br>' +
                              '<strong>Скорость:</strong> ' + speedMbps.toFixed(2) + ' Mbit/s';
    }, 1000);
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
