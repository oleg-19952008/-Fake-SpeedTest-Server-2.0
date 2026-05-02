using System;
using System.Text;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Utility class for Base64URL encoding and decoding operations.
    /// Implements RFC 4648 Section 5 (URL-safe Base64 encoding).
    /// </summary>
    public static class Base64Url
    {
        /// <summary>
        /// Encodes a byte array to a Base64URL string.
        /// Uses URL-safe characters (- instead of +, _ instead of /) and omits padding.
        /// </summary>
        /// <param name="data">Byte array to encode</param>
        /// <returns>Base64URL encoded string without padding</returns>
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
        /// Encodes a UTF-8 string to a Base64URL string.
        /// </summary>
        /// <param name="text">String to encode</param>
        /// <returns>Base64URL encoded string without padding</returns>
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
        /// Decodes a Base64URL string to a byte array.
        /// Handles URL-safe characters and optional padding.
        /// </summary>
        /// <param name="base64Url">Base64URL encoded string</param>
        /// <returns>Decoded byte array</returns>
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
        /// Decodes a Base64URL string to a UTF-8 string.
        /// </summary>
        /// <param name="base64Url">Base64URL encoded string</param>
        /// <returns>Decoded UTF-8 string</returns>
        public static string DecodeToString(string base64Url)
        {
            byte[] data = DecodeToBytes(base64Url);
            return Encoding.UTF8.GetString(data);
        }

        /// <summary>
        /// Validates if a string is a valid Base64URL format.
        /// </summary>
        /// <param name="base64Url">String to validate</param>
        /// <returns>True if valid Base64URL format, false otherwise</returns>
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
