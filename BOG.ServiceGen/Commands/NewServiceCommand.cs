// File: BOG.ServiceGen/Commands/NewServiceCommand.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using BOG.ServiceGen.Helpers;

namespace BOG.ServiceGen.Commands
{
    public class NewServiceSettings : CommandSettings
    {
        [CommandArgument(0, "<Name>")]
        [Description("Name of your Microservice (e.g. Payments)")]
        public string ServiceName { get; set; } = null!;

        [CommandOption("-o|--output <OUTPUT>")]
        [Description("Directory where the solution will be created (default = current folder)")]
        public string? OutputDirectory { get; set; }

        [CommandOption("-d|--dbProvider <DBPROVIDER>")]
        [Description("Database provider: sqlserver | postgresql | oracle (default = postgresql)")]
        [DefaultValue("postgresql")]
        public string DbProvider { get; set; } = "postgresql";

        [CommandOption("-k|--docker")]
        [Description("Include Dockerfiles? (default = true)")]
        [DefaultValue(true)]
        public bool IncludeDocker { get; set; } = true;

        [CommandOption("-t|--tests")]
        [Description("Include a test project? (default = true)")]
        [DefaultValue(true)]
        public bool IncludeTests { get; set; } = true;

        [CommandOption("-s|--serilog")]
        [Description("Use Serilog? (default = true)")]
        [DefaultValue(true)]
        public bool UseSerilog { get; set; } = true;

        [CommandOption("-g|--generateServices")]
        [Description("Generate Entity Services and Controllers? (default = false)")]
        [DefaultValue(false)]
        public bool GenerateServices { get; set; } = false;

        public override ValidationResult Validate()
        {
            var validProviders = new[] { "sqlserver", "postgresql", "oracle" };
            if (!validProviders.Contains(DbProvider.Trim().ToLowerInvariant()))
                return ValidationResult.Error("dbProvider must be one of: sqlserver, postgresql, oracle");

            var name = ServiceName?.Trim() ?? "";
            if (string.IsNullOrEmpty(name))
                return ValidationResult.Error("ServiceName cannot be empty.");

            if (name.Contains('.') || name.Contains(' '))
                return ValidationResult.Error("ServiceName must be a single token (no dots or spaces).");

            return ValidationResult.Success();
        }
    }

    public class NewServiceCommand : Command<NewServiceSettings>
    {
        public override int Execute(CommandContext context, NewServiceSettings settings)
        {
            // 1) Normalize inputs
            var projectName = settings.ServiceName.Trim();
            var outputDir   = string.IsNullOrWhiteSpace(settings.OutputDirectory)
                                ? Directory.GetCurrentDirectory()
                                : settings.OutputDirectory!.Trim();

            var targetPath = Path.Combine(outputDir, projectName);

            try
            {
                // 2) Create the target directory
                Directory.CreateDirectory(targetPath);

                // 3) Build the ProcessStartInfo for `dotnet new bogms …`
                var psi = new ProcessStartInfo
                {
                    FileName               = "dotnet",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };

                psi.ArgumentList.Add("new");
                psi.ArgumentList.Add("bogms");
                psi.ArgumentList.Add("--output");
                psi.ArgumentList.Add(targetPath);
                psi.ArgumentList.Add("--dbProvider");
                psi.ArgumentList.Add(settings.DbProvider.Trim());
                psi.ArgumentList.Add("--name");
                psi.ArgumentList.Add(projectName);
                psi.ArgumentList.Add("--force");
                psi.ArgumentList.Add("--generateServices");
                psi.ArgumentList.Add(settings.GenerateServices ? "true" : "false");

                //
                // ─────── DEBUG: Print full invocation ───────
                //
                var quotedArgs = psi.ArgumentList
                    .Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg);
                AnsiConsole.MarkupLine("[grey]Full invocation:[/]");
                AnsiConsole.MarkupLine($"  dotnet {string.Join(' ', quotedArgs)}");

                AnsiConsole.WriteLine("[grey]Args List:[/]");
                for (var i = 0; i < psi.ArgumentList.Count; i++)
                {
                    var rawArg = psi.ArgumentList[i];
                    AnsiConsole.WriteLine($"  Arg[{i}]: \"{rawArg}\"");
                }
                //
                // ───────────────────────────────────────────────
                //

                // 4) Run the “dotnet new bogms” process
                using var proc = Process.Start(psi)!;
                var stdOutTask = proc.StandardOutput.ReadToEndAsync();
                var stdErrTask = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();

                var stdOut = stdOutTask.GetAwaiter().GetResult();
                var stdErr = stdErrTask.GetAwaiter().GetResult();

                if (!string.IsNullOrWhiteSpace(stdOut))
                {
                    AnsiConsole.MarkupLine("[grey]dotnet-new output:[/]");
                    AnsiConsole.WriteLine(stdOut.TrimEnd());
                }

                if (!string.IsNullOrWhiteSpace(stdErr))
                {
                    AnsiConsole.MarkupLine("[red]dotnet-new error:[/]");
                    AnsiConsole.WriteLine(stdErr.TrimEnd());
                }

                if (proc.ExitCode != 0)
                {
                    AnsiConsole.MarkupLine("\n[bold red]Failed to create microservice.[/]");
                    return 1;
                }

                // 5) Post-processing: Docker, Tests, Serilog, EF injection, Entity+Controller generation
                if (settings.IncludeDocker)
                    ScaffoldingHelpers.EnableDockerSupport(targetPath);

                if (settings.IncludeTests)
                    ScaffoldingHelpers.AddTestProject(targetPath, projectName);

                if (settings.UseSerilog)
                    ScaffoldingHelpers.EnsureSerilogConfiguration(targetPath);

                ScaffoldingHelpers.InjectEfProvider(
                    targetPath,
                    projectName,
                    settings.DbProvider.Trim());

                ScaffoldingHelpers.InjectEfRegistration(
                    targetPath,
                    projectName,
                    settings.DbProvider.Trim());

                if (settings.GenerateServices)
                {
                    ScaffoldingHelpers
                        .GenerateEntityServicesIncrementally(
                            targetPath,
                            projectName,
                            settings.DbProvider.Trim())
                        .GetAwaiter()
                        .GetResult();
                }

                AnsiConsole.MarkupLine("\n[bold green]Scaffolding complete![/]");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] {ex.Message}");
                return 1;
            }
        }
    }
}
