using System.Text;
using BOG.Microservice.Scaffolder;
using Spectre.Console;
using Spectre.Console.Cli;
using BOG.ServiceGen.Commands;

namespace BOG.ServiceGen
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // If the user launched "bogms" with no arguments, show the interactive menu.
            // Otherwise, delegate to Spectre.Console.Cli so that "bogms new …",
            // "bogms add-entity …", or "bogms add-controller …" still work.
            if (args.Length == 0)
            {
                RunInteractiveMenu();
                return 0;
            }
            else
            {
                return RunCliMode(args);
            }
        }

        private static int RunCliMode(string[] args)
        {
            var app = new CommandApp();

            app.Configure(config =>
            {
                config.SetApplicationName("bogms");

                // Register the three subcommands as before
                config.AddCommand<NewServiceCommand>("new")
                      .WithDescription("Create a new microservice from scratch");

                config.AddCommand<AddEntityCommand>("add-entity")
                      .WithDescription("Generate or update entity services for new entities");

                config.AddCommand<AddControllerCommand>("add-controller")
                      .WithDescription("Scaffold REST controllers for specified entities");
            });

            return app.Run(args);
        }

        private static void RunInteractiveMenu()
        {
            Console.Clear();
            AsciiArt.PrintBanner(); // If you had a static AsciiArt class that prints your banner

            while (true)
            {
                // 1) Show a selection prompt
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold green]Bank of Georgia MicroService Generator[/]")
                        .PageSize(10)
                        .MoreChoicesText("[grey](Move up and down to reveal more options)[/]")
                        .AddChoices(new[]
                        {
                            "🆕 Create New Microservice",
                            "➕ Add New Entity to Existing Microservice",
                            "🛠️  Scaffold Controller for Entity(ies)",
                            "ℹ️  About Bank of Georgia",
                            "🚪 Exit"
                        }));

                Console.Clear();
                switch (choice)
                {
                    case "🆕 Create New Microservice":
                        InteractiveCreateNewService();
                        break;

                    case "➕ Add New Entity to Existing Microservice":
                        InteractiveAddEntity();
                        break;

                    case "🛠️  Scaffold Controller for Entity(ies)":
                        InteractiveAddController();
                        break;

                    case "ℹ️  About Bank of Georgia":
                        ShowAboutScreen();
                        break;

                    case "🚪 Exit":
                        AnsiConsole.MarkupLine("[bold yellow]Goodbye![/]");
                        return;
                }

                // After each action, pause and then re‐print banner & menu
                AnsiConsole.MarkupLine("\n[grey]Press any key to return to main menu…[/]");
                Console.ReadKey(true);
                Console.Clear();
                AsciiArt.PrintBanner();
            }
        }

        private static void InteractiveCreateNewService()
        {
            AsciiArt.PrintBanner();
            AnsiConsole.MarkupLine("\n[bold aqua]Create New Microservice Wizard[/]\n");

            // 1) Prompt for Microservice Name
            var projectName = AnsiConsole.Ask<string>(
                "Enter the [green]name of your Microservice[/] (e.g. Payments):");

            // 2) Prompt for Target Directory (default = current directory)
            var solutionDirectory = AnsiConsole.Ask<string>(
                "Enter the [green]solution directory[/] (default = current folder):",
                Directory.GetCurrentDirectory());

            // 3) DB Provider choice
            var dbProvider = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Select DB provider[/]:")
                    .AddChoices(new[] { "postgresql", "sqlserver", "oracle" }));

            // 4) Confirm flags
            var enableDocker  = AnsiConsole.Confirm("[green]Include Dockerfiles?[/]", true);
            var enableTests   = AnsiConsole.Confirm("[green]Include a test project?[/]", true);
            var enableSerilog = AnsiConsole.Confirm("[green]Use Serilog?[/]", true);
            var generateSvcs  = AnsiConsole.Confirm("[green]Generate Entity Services?[/]", false);

            // 5) Summary table
            var tbl = new Table().Border(TableBorder.Rounded)
                                .AddColumn("[yellow]Setting[/]")
                                .AddColumn("[yellow]Value[/]");

            tbl.AddRow("Microservice Name", projectName);
            tbl.AddRow("Target Dir", solutionDirectory);
            tbl.AddRow("DB Provider", dbProvider);
            tbl.AddRow("Docker", enableDocker.ToString());
            tbl.AddRow("Tests", enableTests.ToString());
            tbl.AddRow("Serilog", enableSerilog.ToString());
            tbl.AddRow("GenSvcs", generateSvcs.ToString());

            AnsiConsole.Write(tbl);

            // 6) Build settings object to pass into NewServiceCommand
            var settings = new NewServiceSettings
            {
                ServiceName      = projectName,
                OutputDirectory  = solutionDirectory,
                DbProvider       = dbProvider,
                IncludeDocker    = enableDocker,
                IncludeTests     = enableTests,
                UseSerilog       = enableSerilog,
                GenerateServices = generateSvcs
            };

            // 7) Execute the command (passing null as CommandContext is OK)
            var cmd = new NewServiceCommand();
            var exitCode = cmd.Execute(null!, settings);

            if (exitCode == 0)
                AnsiConsole.MarkupLine("\n[bold green]Microservice created successfully![/]");
            else
                AnsiConsole.MarkupLine("\n[bold red]Failed to create microservice.[/]");
        }

        private static void InteractiveAddEntity()
        {
            AsciiArt.PrintBanner();
            AnsiConsole.MarkupLine("\n[bold aqua]Add New Entity Wizard[/]\n");

            // 1) Prompt for the existing microservice root
            var projectRoot = AnsiConsole.Ask<string>(
                "Enter the [green]path to your microservice root[/] (where the .sln lives):",
                Directory.GetCurrentDirectory());

            // 2) Prompt for DB Provider (the same you used when you ran "new")
            var dbProvider = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Select DB provider (used previously)[/]:")
                    .AddChoices(new[] { "postgresql", "sqlserver", "oracle" }));

            // 3) Show summary
            var tbl = new Table().Border(TableBorder.Rounded)
                                .AddColumn("[yellow]Setting[/]")
                                .AddColumn("[yellow]Value[/]");
            tbl.AddRow("Microservice Root", projectRoot);
            tbl.AddRow("DB Provider", dbProvider);
            AnsiConsole.Write(tbl);

            // 4) Ask to proceed
            if (!AnsiConsole.Confirm("[green]Proceed with generating new entities?[/]", true))
            {
                AnsiConsole.MarkupLine("[yellow]Aborted entity generation.[/]");
                return;
            }

            // 5) Execute AddEntityCommand
            var settings = new AddEntitySettings
            {
                ProjectRoot = projectRoot,
                DbProvider  = dbProvider
            };

            var cmd = new AddEntityCommand();
            var exitCode = cmd.Execute(null!, settings);

            if (exitCode == 0)
                AnsiConsole.MarkupLine("\n[bold green]Entity generation complete![/]");
            else
                AnsiConsole.MarkupLine("\n[bold red]Failed to generate entities.[/]");
        }

        private static void InteractiveAddController()
        {
            AsciiArt.PrintBanner();
            AnsiConsole.MarkupLine("\n[bold aqua]Scaffold Controller Wizard[/]\n");

            // 1) Prompt for the microservice root
            var projectRoot = AnsiConsole.Ask<string>(
                "Enter the [green]path to your microservice root[/] (where the .sln lives):",
                Directory.GetCurrentDirectory());

            // 2) Prompt for one or more entity names (comma-separated)
            var entityNames = AnsiConsole.Ask<string>(
                "Enter comma-separated [green]entity names[/] (e.g. Customer,Order):");

            // 3) Summary
            var tbl = new Table().Border(TableBorder.Rounded)
                                .AddColumn("[yellow]Setting[/]")
                                .AddColumn("[yellow]Value[/]");
            tbl.AddRow("Microservice Root", projectRoot);
            tbl.AddRow("Entities", entityNames);
            AnsiConsole.Write(tbl);

            // 4) Ask to proceed
            if (!AnsiConsole.Confirm("[green]Proceed with scaffolding controller(s)?[/]", true))
            {
                AnsiConsole.MarkupLine("[yellow]Aborted controller generation.[/]");
                return;
            }

            // 5) Execute AddControllerCommand
            var settings = new AddControllerSettings
            {
                ProjectRoot = projectRoot,
                EntityNames = entityNames
            };

            var cmd = new AddControllerCommand();
            var exitCode = cmd.Execute(null!, settings);

            if (exitCode == 0)
                AnsiConsole.MarkupLine("\n[bold green]Controller scaffolding complete![/]");
            else
                AnsiConsole.MarkupLine("\n[bold red]Failed to scaffold controllers.[/]");
        }

        private static void ShowAboutScreen()
        {
            AsciiArt.PrintBanner();

            var panelText = new Panel(@"
[bold]Bank of Georgia[/] 
(საქართველოს ბანკი)

We are a leading financial institution 
with a commitment to innovation, 
empowering individuals, businesses, 
and communities across Georgia.
");
            panelText.Header("About Bank of Georgia");
            panelText.Border = BoxBorder.Double;
            panelText.Padding = new Padding(1, 1);

            AnsiConsole.Write(panelText);
        }
    }
}
