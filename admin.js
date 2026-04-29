// Админ-панель для управления банами
// Доступна только с IP адреса 2.2.2.103

const ALLOWED_IP = '2.2.2.103';
const TARGET_IP_TO_UNBAN = '89.109.20.234';
const API_BASE_URL = '/api/admin';

// Элементы DOM
const adminPanel = document.getElementById('adminPanel');
const accessDenied = document.getElementById('accessDenied');
const clientIpElement = document.getElementById('clientIp');
const accessStatusElement = document.getElementById('accessStatus');
const deniedIpElement = document.getElementById('deniedIp');
const messageElement = document.getElementById('message');
const ipToUnbanInput = document.getElementById('ipToUnban');
const ipToBanInput = document.getElementById('ipToBan');

/**
 * Получает IP адрес клиента через API
 */
async function getClientIP() {
    try {
        const response = await fetch(`${API_BASE_URL}/ip`);
        if (!response.ok) throw new Error('Failed to get IP');
        const data = await response.json();
        return data.ip;
    } catch (error) {
        console.error('Error getting client IP:', error);
        // Fallback - пытаемся получить через сторонний сервис
        try {
            const response = await fetch('https://api.ipify.org?format=json');
            if (!response.ok) throw new Error('Failed to get IP from ipify');
            const data = await response.json();
            return data.ip;
        } catch (fallbackError) {
            console.error('Fallback failed:', fallbackError);
            return 'unknown';
        }
    }
}

/**
 * Проверяет доступ к админ-панели
 */
async function checkAccess() {
    const clientIp = await getClientIP();
    clientIpElement.textContent = clientIp;
    
    if (clientIp === ALLOWED_IP) {
        accessStatusElement.textContent = 'Разрешён';
        accessStatusElement.style.color = '#2ecc71';
        adminPanel.style.display = 'block';
        accessDenied.style.display = 'none';
        
        // Автоматически устанавливаем целевой IP
        ipToUnbanInput.value = TARGET_IP_TO_UNBAN;
        
        showMessage(`Доступ разрешён. Целевой IP: ${TARGET_IP_TO_UNBAN}`, 'success');
    } else {
        accessStatusElement.textContent = 'Запрещён';
        accessStatusElement.style.color = '#e74c3c';
        adminPanel.style.display = 'none';
        accessDenied.style.display = 'block';
        deniedIpElement.textContent = clientIp;
        
        showMessage('Доступ запрещён. Ваш IP не входит в список разрешённых.', 'error');
    }
}

/**
 * Снимает бан с указанного IP
 */
async function unbanIP() {
    const ip = ipToUnbanInput.value.trim();
    
    if (!ip) {
        showMessage('Введите IP адрес', 'error');
        return;
    }
    
    // Валидация IP адреса
    if (!isValidIP(ip)) {
        showMessage('Некорректный формат IP адреса', 'error');
        return;
    }
    
    try {
        const response = await fetch(`${API_BASE_URL}/unban`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ ip: ip })
        });
        
        const result = await response.json();
        
        if (result.success) {
            showMessage(`Бан успешно снят с IP: ${ip}`, 'success');
        } else {
            showMessage(`Ошибка: ${result.message || 'Не удалось снять бан'}`, 'error');
        }
    } catch (error) {
        console.error('Error unbanning IP:', error);
        showMessage('Ошибка соединения с сервером', 'error');
    }
}

/**
 * Устанавливает бан на указанный IP
 */
async function banIP() {
    const ip = ipToBanInput.value.trim();
    
    if (!ip) {
        showMessage('Введите IP адрес', 'error');
        return;
    }
    
    // Валидация IP адреса
    if (!isValidIP(ip)) {
        showMessage('Некорректный формат IP адреса', 'error');
        return;
    }
    
    try {
        const response = await fetch(`${API_BASE_URL}/ban`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ 
                ip: ip,
                duration: 'permanent' // или можно указать длительность
            })
        });
        
        const result = await response.json();
        
        if (result.success) {
            showMessage(`IP ${ip} успешно заблокирован`, 'success');
        } else {
            showMessage(`Ошибка: ${result.message || 'Не удалось заблокировать'}`, 'error');
        }
    } catch (error) {
        console.error('Error banning IP:', error);
        showMessage('Ошибка соединения с сервером', 'error');
    }
}

/**
 * Проверяет корректность IP адреса
 */
function isValidIP(ip) {
    const ipv4Pattern = /^(\d{1,3}\.){3}\d{1,3}$/;
    if (!ipv4Pattern.test(ip)) return false;
    
    const parts = ip.split('.');
    for (let part of parts) {
        const num = parseInt(part, 10);
        if (num < 0 || num > 255) return false;
    }
    
    return true;
}

/**
 * Показывает сообщение пользователю
 */
function showMessage(text, type) {
    messageElement.textContent = text;
    messageElement.className = `message ${type}`;
    
    // Автоматически скрываем сообщение через 5 секунд
    setTimeout(() => {
        messageElement.className = 'message';
    }, 5000);
}

/**
 * Инициализация при загрузке страницы
 */
document.addEventListener('DOMContentLoaded', () => {
    checkAccess();
});

// Периодическая проверка доступа (каждые 30 секунд)
setInterval(checkAccess, 30000);
