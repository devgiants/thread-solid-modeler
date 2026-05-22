using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Inventor;

namespace ThreadModeler.Utilities
{
    internal static class DebugLog
    {
        private static readonly object _sync = new object();
        private static string _logFilePath;

        public static void Initialize(string assemblyLocation)
        {
#if DEBUG
            try
            {
                string directory = System.IO.Path.GetDirectoryName(assemblyLocation);
                string logsDir = System.IO.Path.Combine(directory, "logs");

                System.IO.Directory.CreateDirectory(logsDir);

                _logFilePath = System.IO.Path.Combine(logsDir, "threadmodeler-debug.log");

                WriteLine("=== ThreadModeler debug log started ===");
                WriteLine("Assembly: " + assemblyLocation);
                WriteLine("Machine: " + System.Environment.MachineName);
                WriteLine("OS: " + System.Environment.OSVersion);
            }
            catch
            {
            }
#endif
        }

        public static void WriteLine(string message)
        {
#if DEBUG
            try
            {
                if (string.IsNullOrEmpty(_logFilePath))
                    return;

                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                    System.DateTime.Now,
                    System.Threading.Thread.CurrentThread.ManagedThreadId,
                    message,
                    System.Environment.NewLine);

                lock (_sync)
                {
                    System.IO.File.AppendAllText(_logFilePath, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
#endif
        }

        public static void WriteException(string context, Exception ex)
        {
#if DEBUG
            WriteLine(context + System.Environment.NewLine + ex);
#endif
        }

        public static void WriteInventorPoint(string label, Point point)
        {
#if DEBUG
            if (point == null)
            {
                WriteLine(label + " = <null>");
                return;
            }

            WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} = ({1:0.###}, {2:0.###}, {3:0.###})",
                label,
                point.X,
                point.Y,
                point.Z));
#endif
        }

        public static void WriteInventorVector(string label, Vector vector)
        {
#if DEBUG
            if (vector == null)
            {
                WriteLine(label + " = <null>");
                return;
            }

            WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} = ({1:0.###}, {2:0.###}, {3:0.###}) len={4:0.###}",
                label,
                vector.X,
                vector.Y,
                vector.Z,
                vector.Length));
#endif
        }

        public static void WriteParameter(string label, Parameter parameter)
        {
#if DEBUG
            if (parameter == null)
            {
                WriteLine(label + " = <null>");
                return;
            }

            WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} = {1} (name={2})",
                label,
                parameter.Value,
                parameter.Name));
#endif
        }

        public static void WriteValue(string label, object value)
        {
#if DEBUG
            WriteLine(label + " = " + (value == null ? "<null>" : value.ToString()));
#endif
        }
    }
}
