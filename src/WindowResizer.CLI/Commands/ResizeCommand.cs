using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;
using WindowResizer.Base;
using WindowResizer.CLI.Utils;

namespace WindowResizer.CLI.Commands
{
    internal class ResizeCommand : Command
    {
        public ResizeCommand() : base("resize", "Resize window by process/title, or use -w/--window for direct placement without config.")
        {
            var configOption = new ConfigOption();
            AddOption(configOption);
            var profileOption = new ProfileOption();
            AddOption(profileOption);
            var processOption = new ProcessOption();
            AddOption(processOption);
            var titleOption = new TitleOption();
            AddOption(titleOption);
            var windowOption = new WindowOption();
            AddOption(windowOption);
            var verboseOption = new VerboseOption();
            AddOption(verboseOption);

            this.SetHandler((InvocationContext context) =>
            {
                var config = context.ParseResult.GetValueForOption(configOption);
                var profile = context.ParseResult.GetValueForOption(profileOption);
                var process = context.ParseResult.GetValueForOption(processOption);
                var title = context.ParseResult.GetValueForOption(titleOption);
                var verbose = context.ParseResult.GetValueForOption(verboseOption);
                var windowOptionWasUsed = context.ParseResult.FindResultFor(windowOption) != null;
                var windowArguments = context.ParseResult.GetValueForOption(windowOption) ?? new int[0];

                void VerboseInfo(List<WindowCmd.TargetWindow> lists)
                {
                    if (verbose)
                    {
                        Verbose(lists);
                    }
                }

                var success = windowOptionWasUsed
                    ? WindowCmd.ResizeDirect(process, title, windowArguments, Output.Error, VerboseInfo)
                    : WindowCmd.Resize(config?.FullName, profile, process, title, Output.Error, VerboseInfo);

                context.ExitCode = success ? 0 : 1;
                return Task.CompletedTask;
            });
        }

        private static void Verbose(List<WindowCmd.TargetWindow> lists)
        {
            if (!lists.Any())
            {
                Output.Echo("No windows resized.");
                return;
            }

            var table = new Table();
            table.AddColumn(new TableColumn("Handle"));
            table.AddColumn(new TableColumn("Process"));
            table.AddColumn(new TableColumn("Title"));
            table.AddColumn(new TableColumn("Success").Centered());
            table.AddColumn(new TableColumn("Error"));
            foreach (var item in lists)
            {
                var result = string.IsNullOrEmpty(item.Result) ? "[green]Y[/]" : "[red]N[/]";
                table.AddRow(item.Handle.ToString(), $"[green]{item.ProcessName}[/]", item.Title ?? string.Empty, result, $"[red]{item.Result}[/]");
            }

            table.Border(TableBorder.Square);
            table.Alignment(Justify.Left);
            AnsiConsole.Write(table);
        }
    }
}
