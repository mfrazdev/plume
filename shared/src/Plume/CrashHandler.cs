using System.Diagnostics;

namespace Plume.Logging
{
    public static class CrashHandler
    {
        private const string RED = "\x1B[31m";
        private const string YELLOW = "\x1B[33m";
        private const string CYAN = "\x1B[36m";
        private const string BOLD = "\x1B[1m";
        private const string RESET = "\x1B[0m";

        public static void Install()
        {
            // Captura erros da thread principal e threads síncronas
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    HandleException(ex, Thread.CurrentThread.Name ?? "Main");
                }
            };

            // Captura erros de Tasks assíncronas perdidas
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                HandleException(args.Exception, Thread.CurrentThread.Name ?? "Task");
                args.SetObserved(); // Impede a aplicação de crashar por Task perdida
            };
        }

        // Método público para ser usado pelo nosso Logger quando ele recebe um erro
        public static void HandleException(Exception ex, string threadName, bool isCause = false)
        {
            var prefix = isCause ? "CAUSED BY" : "EXCEPTION";
            var simpleName = ex.GetType().Name;
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? "" : $": {ex.Message}";

            Console.WriteLine();
            Console.WriteLine($"{RED}{BOLD}[ {prefix} ]{RESET} {YELLOW}{threadName}{RESET} | {RED}{simpleName}{RESET}{msg}");

            var st = new StackTrace(ex, true);
            var frames = st.GetFrames() ?? Array.Empty<StackFrame>();
            
            bool foundUserCode = false;
            var outputLines = new List<string>();

            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method == null) continue;

                var declaringType = method.DeclaringType?.FullName ?? "";
                bool isSystem = declaringType.StartsWith("System.") || 
                                declaringType.StartsWith("Microsoft.");

                if (!isSystem) foundUserCode = true;

                // Se for sistema, pula APENAS SE já achou código do usuário antes
                if (isSystem && foundUserCode) continue;

                var rawFileName = frame.GetFileName();
                var fileName = !string.IsNullOrEmpty(rawFileName) 
                    ? Path.GetFileNameWithoutExtension(rawFileName) 
                    : (declaringType.Split('.').LastOrDefault() ?? declaringType);
                
                var lineNumber = frame.GetFileLineNumber();
                var lineStr = lineNumber > 0 ? lineNumber.ToString() : "Unknown";

                outputLines.Add($"  {CYAN}{fileName}{RESET} : {RED}{lineStr}{RESET}");
                
                if (outputLines.Count >= 15) break;
            }

            foreach (var line in outputLines)
            {
                Console.WriteLine(line);
            }

            // Exceções internas (Caused by)
            if (ex.InnerException != null)
            {
                HandleException(ex.InnerException, threadName, true);
            }
        }
    }
}