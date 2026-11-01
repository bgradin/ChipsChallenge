using System.Runtime.InteropServices;
using Translator.Delivery;

namespace ChipShift64;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            // Attach for interactive console use; inherited redirected pipes work without it.
            AttachConsole(unchecked((uint)-1));
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };
            return TranslationCommand.RunAsync(args, Console.Out, Console.Error, cancellation.Token).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TranslationForm());
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
}
