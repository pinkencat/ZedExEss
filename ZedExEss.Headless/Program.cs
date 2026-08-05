using ZedExEss.Diagnostics;

namespace ZedExEss.Headless
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || Array.Exists(args, IsHelpSwitch))
            {
                Console.WriteLine(DiagnosticCommandLine.HelpText);
                return args.Length == 0 ? 2 : 0;
            }

            try
            {
                if (!DiagnosticCommandLine.TryRun(args, out int exitCode))
                {
                    Console.Error.WriteLine("No diagnostic was selected.\n");
                    Console.Error.WriteLine(DiagnosticCommandLine.HelpText);
                    return 2;
                }

                return exitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static bool IsHelpSwitch(string value)
        {
            return value.Equals("--help", StringComparison.OrdinalIgnoreCase)
                || value.Equals("-h", StringComparison.OrdinalIgnoreCase)
                || value.Equals("-?", StringComparison.OrdinalIgnoreCase);
        }
    }
}
