using System;
using System.IO;
using System.Text;
using System.Threading;

namespace FakeSpeedTestServer
{
    /// <summary>
    /// Пользовательский TextWriter, который пишет одновременно в консоль и в файл логов.
    /// Позволяет перехватывать все вызовы Console.WriteLine() и дублировать их в лог-файл.
    /// </summary>
    public class ConsoleAndFileWriter : TextWriter
    {
        private readonly TextWriter _consoleWriter;
        private readonly string _logFilePath;
        private readonly object _writeLock = new object();
        private static readonly Encoding FileEncoding = Encoding.UTF8;
        private static readonly Encoding ConsoleEncoding = Encoding.GetEncoding(866);

        /// <summary>
        /// Инициализирует новый экземпляр класса ConsoleAndFileWriter.
        /// </summary>
        /// <param name="logFilePath">Путь к файлу логов</param>
        public ConsoleAndFileWriter(string logFilePath)
        {
            _logFilePath = logFilePath;
            _consoleWriter = Console.Out;
        }

        /// <summary>
        /// Возвращает кодировку UTF-8 для корректного отображения кириллицы.
        /// </summary>
        public override Encoding Encoding => ConsoleEncoding;

        /// <summary>
        /// Записывает символ в консоль и файл логов.
        /// </summary>
        public override void Write(char value)
        {
            lock (_writeLock)
            {
                _consoleWriter.Write(value);
                File.AppendAllText(_logFilePath, value.ToString(), FileEncoding);
            }
        }

        /// <summary>
        /// Записывает строку в консоль и файл логов.
        /// </summary>
        public override void Write(string value)
        {
            lock (_writeLock)
            {
                _consoleWriter.Write(value);
                File.AppendAllText(_logFilePath, value, FileEncoding);
            }
        }

        /// <summary>
        /// Записывает строку с завершением строки в консоль и файл логов.
        /// </summary>
        public override void WriteLine(string value)
        {
            lock (_writeLock)
            {
                var timestamp = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
                var logEntry = $"[{timestamp}] {value}";
                
                _consoleWriter.WriteLine(logEntry);
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, FileEncoding);
            }
        }

        /// <summary>
        /// Записывает объект в консоль и файл логов.
        /// </summary>
        public override void WriteLine(object value)
        {
            WriteLine(value?.ToString());
        }

        /// <summary>
        /// Записывает форматированную строку в консоль и файл логов.
        /// </summary>
        public override void Write(string format, params object[] args)
        {
            Write(string.Format(format, args));
        }

        /// <summary>
        /// Записывает форматированную строку с завершением строки в консоль и файл логов.
        /// </summary>
        public override void WriteLine(string format, params object[] args)
        {
            WriteLine(string.Format(format, args));
        }

        /// <summary>
        /// Асинхронно записывает строку в консоль и файл логов.
        /// </summary>
        public override System.Threading.Tasks.Task WriteLineAsync(string value)
        {
            return System.Threading.Tasks.Task.Run(() => WriteLine(value));
        }

        /// <summary>
        /// Освобождает ресурсы.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _consoleWriter?.Flush();
            }
            base.Dispose(disposing);
        }
    }
}
