using System.CommandLine;
using System.IO;
using System.Linq;

namespace WindowResizer.CLI.Commands
{
    public class ConfigOption : Option<FileInfo>
    {
        public ConfigOption() : base(
            aliases: new[]
            {
                "--config",
                "-c"
            },
            description: "Config file path, use current config file if omitted.",
            parseArgument: result =>
            {
                if (result.Tokens.Count == 0)
                {
                    return null;
                }

                var filePath = result.Tokens.Single().Value;
                return new FileInfo(filePath);
            })
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }

    public class ProfileOption : Option<string>
    {
        public ProfileOption() : base(
            aliases: new[]
            {
                "--profile",
                "-P"
            },
            description: "Profile name, use current profile if omitted.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }

    public class ProcessOption : Option<string>
    {
        public ProcessOption() : base(
            aliases: new[]
            {
                "--process",
                "-p"
            },
            description: "Process name partial match, for example -p note matches notepad.exe. Uses the foreground process if omitted.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }

    public class TitleOption : Option<string>
    {
        public TitleOption() : base(
            aliases: new[]
            {
                "--title",
                "-t"
            },
            description: "Process title, all windows of the process will be resized if not specified.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }

    public class WindowOption : Option<int[]>
    {
        public WindowOption() : base(
            aliases: new[]
            {
                "--window",
                "-w"
            },
            description: "Direct window placement, skipping config: -w centers/resizes the window; -w left top moves it and keeps current size; -w left top right keeps current height; -w left top right bottom sets the full rectangle. Uses the foreground window when --process is omitted.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = true;
            Arity = ArgumentArity.ZeroOrMore;

            AddValidator(result =>
            {
                if (result.Tokens.Count > 4)
                {
                    result.ErrorMessage = "The -w/--window option accepts at most four numbers: left top right bottom.";
                }
            });
        }
    }

    public class InteractiveOption : Option<bool>
    {
        public InteractiveOption() : base(
            aliases: new[]
            {
                "--interactive",
                "-i"
            },
            description: "Interactively choose a visible top-level window/application before resizing. Use arrow keys, mouse wheel, single-click to highlight, double-click to choose, letter keys, and Enter, or Esc to quit. Shows PID and marks the frontmost listed window for each process name as Top. The selected row uses a dark inverted highlight background.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }

    public class VerboseOption : Option<bool>
    {
        public VerboseOption() : base(
            aliases: new[]
            {
                "--verbose",
                "-v"
            },
            description: "Show more details.")
        {
            IsRequired = false;
            AllowMultipleArgumentsPerToken = false;
        }
    }
}
