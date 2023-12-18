using System.Diagnostics;
using System.Runtime.CompilerServices;
using Colossal.Logging;

namespace Traffic
{
    public static class Logger
    {
        private static ILog _log = LogManager.GetLogger($"{nameof(Traffic)}.{nameof(Mod)}", false);

        public static void Info(string message, [CallerMemberName]string methodName = null) {
            _log.Info(message);
        }
        
        [Conditional("DEBUG")]
        public static void Debug(string message) {
            _log.Info(message);
        }

        public static void Warning(string message) {
            _log.Warn(message);
        }

        public static void Error(string message) {
            _log.Error(message);
        }
    }
}
