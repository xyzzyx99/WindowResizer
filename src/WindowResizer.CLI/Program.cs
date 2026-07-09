using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;
using WindowResizer.CLI.Commands;
using WindowResizer.CLI.Utils;

namespace WindowResizer.CLI
{
    internal static class Program
    {
        static Task<int> Main(string[] args)
        {
            System.Console.OutputEncoding = System.Text.Encoding.UTF8;
            args = UseResizeCommandByDefault(args);

            var rootCommand = new RootCommand($"{nameof(WindowResizer)} CLI. The resize command is the default, so the command name may be omitted.");
            rootCommand.AddCommand(new ResizeCommand());

            var parser = new CommandLineBuilder(rootCommand)
                         .UseDefaults()
                         .UseExceptionHandler((e, _) =>
                         {
                             Output.Error(e.Message);
                         }, 1)
                         .UseHelp(ctx =>
                         {
                             ctx.HelpBuilder.CustomizeLayout(
                                 c =>
                                     HelpBuilder.Default
                                                .GetLayout()
                                                .Skip(1)
                                                .Prepend(p =>
                                                {
                                                    AnsiConsole.Write(new FigletText(nameof(WindowResizer)).LeftJustified().Color(Color.Blue));
                                                    AnsiConsole.MarkupLine("[grey]Default command: resize. You may omit the command name, for example: -i or -w 0 0 800 600.[/]");
                                                    AnsiConsole.WriteLine();
                                                })
                             );
                         })
                         .Build();

            return parser.InvokeAsync(args);
        }

        private static string[] UseResizeCommandByDefault(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return new[] { "resize" };
            }

            var first = args[0];

            if (string.Equals(first, "resize", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "help", StringComparison.OrdinalIgnoreCase))
            {
                return args;
            }

            return new[] { "resize" }.Concat(args).ToArray();
        }

    }
}
