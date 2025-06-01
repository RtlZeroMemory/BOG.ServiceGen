using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using BOG.ServiceGen.Helpers;

namespace BOG.ServiceGen.Commands
{
    /// <summary>
    /// Settings for "bogms add-entity".
    /// </summary>
    public class AddEntitySettings : CommandSettings
    {
        [CommandOption("-p|--path <PROJPATH>")]
        [Description("Path to the root of the microservice solution (where the .sln resides)")]
        [DefaultValue("")]
        public string ProjectRoot { get; set; } = Directory.GetCurrentDirectory();

        [CommandOption("-d|--dbProvider <DBPROVIDER>")]
        [Description("Database provider used when scaffolding (sqlserver|postgresql|oracle)")]
        [DefaultValue("postgresql")]
        public string DbProvider { get; set; } = "postgresql";

        public override ValidationResult Validate()
        {
            var valid = new[] { "sqlserver", "postgresql", "oracle" };
            if (!valid.Contains(DbProvider.ToLowerInvariant()))
                return ValidationResult.Error("dbProvider must be one of: sqlserver, postgresql, oracle");

            if (string.IsNullOrWhiteSpace(ProjectRoot))
                return ValidationResult.Error("ProjectRoot cannot be empty.");

            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Implements "bogms add-entity" logic:
    ///   • Runs the incremental entity generator (ScaffoldingHelpers.GenerateEntityServicesIncrementally)
    /// </summary>
    public class AddEntityCommand : Command<AddEntitySettings>
    {
        public override int Execute(CommandContext context, AddEntitySettings settings)
        {
            var root = settings.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar);
            var projectName = Path.GetFileName(root);
            if (string.IsNullOrWhiteSpace(projectName))
            {
                AnsiConsole.MarkupLine("[red]ERROR:[/] Could not determine project name from path.");
                return 1;
            }

            try
            {
                ScaffoldingHelpers.GenerateEntityServicesIncrementally(root, projectName, settings.DbProvider)
                    .GetAwaiter()
                    .GetResult();

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
