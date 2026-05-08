using System;
using System.Text;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Вспомогательный класс для операций кодирования и декодирования Base64URL.
    /// Реализует RFC 4648, раздел 5 (URL-безопасное кодирование Base64).
    /// </summary>
    public static class Base64Url
    {
        /// <summary>
        /// Кодирует массив байтов в строку Base64URL.
        /// Использует URL-безопасные символы (- вместо +, _ вместо /) и опускает заполнение.
        /// </summary>
        /// <param name="data">Массив байтов для кодирования</param>
        /// <returns>Кодированная строка Base64URL без заполнения</returns>
        public static string Encode(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            string base64 = Convert.ToBase64String(data);
            
            // Convert to URL-safe format: replace '+' with '-', '/' with '_', and remove padding '='
            return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        /// <summary>
        /// Кодирует UTF-8 строку в строку Base64URL.
        /// </summary>
        /// <param name="text">Строка для кодирования</param>
        /// <returns>Кодированная строка Base64URL без заполнения</returns>
        public static string Encode(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            byte[] data = Encoding.UTF8.GetBytes(text);
            return Encode(data);
        }

        /// <summary>
        /// Декодирует строку Base64URL в массив байтов.
        /// Обрабатывает URL-безопасные символы и необязательное заполнение.
        /// </summary>
        /// <param name="base64Url">Кодированная строка Base64URL</param>
        /// <returns>Декодированный массив байтов</returns>
        public static byte[] DecodeToBytes(string base64Url)
        {
            if (base64Url == null)
            {
                throw new ArgumentNullException(nameof(base64Url));
            }

            string base64 = base64Url.Replace('-', '+').Replace('_', '/');
            
            // Add padding if necessary
            switch (base64.Length % 4)
            {
                case 0:
                    break; // No padding needed
                case 2:
                    base64 += "==";
                    break;
                case 3:
                    base64 += "=";
                    break;
                default:
                    throw new FormatException("Invalid Base64URL string");
            }

            return Convert.FromBase64String(base64);
        }

        /// <summary>
        /// Декодирует строку Base64URL в UTF-8 строку.
        /// </summary>
        /// <param name="base64Url">Кодированная строка Base64URL</param>
        /// <returns>Декодированная UTF-8 строка</returns>
        public static string DecodeToString(string base64Url)
        {
            byte[] data = DecodeToBytes(base64Url);
            return Encoding.UTF8.GetString(data);
        }

        /// <summary>
        /// Проверяет, является ли строка допустимым форматом Base64URL.
        /// </summary>
        /// <param name="base64Url">Строка для проверки</param>
        /// <returns>True, если формат Base64URL допустим, иначе false</returns>
        public static bool IsValid(string base64Url)
        {
            if (string.IsNullOrEmpty(base64Url))
            {
                return false;
            }

            // Check for valid Base64URL characters (A-Z, a-z, 0-9, -, _)
            foreach (char c in base64Url)
            {
                if (!((c >= 'A' && c <= 'Z') || 
                      (c >= 'a' && c <= 'z') || 
                      (c >= '0' && c <= '9') || 
                      c == '-' || c == '_'))
                {
                    return false;
                }
            }

            try
            {
                DecodeToBytes(base64Url);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
