using System.Diagnostics;
using BOG.Microservice.Scaffolder;
using Spectre.Console;

namespace BOG.ServiceGen
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            // Print the ASCII banner once at startup
            AsciiArt.PrintBanner();

            // Main menu loop
            while (true)
            {
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[green]Select an action[/]:")
                        .AddChoices(new[]
                        {
                            "Create New Microservice",
                            "About Bank of Georgia",
                            "Exit"
                        }));

                switch (choice)
                {
                    case "Create New Microservice":
                        CreateMicroserviceFlow();
                        break;
                    case "About Bank of Georgia":
                        ShowAboutScreen();
                        break;
                    case "Exit":
                        AnsiConsole.MarkupLine("[bold yellow]Exiting...[/]");
                        return; // quit the application
                }
            }
        }

        /// <summary>
        /// This method orchestrates the prompts for scaffolding a new microservice.
        /// </summary>
        private static void CreateMicroserviceFlow()
        {
            // Clear and re-print banner
            AsciiArt.PrintBanner();
            AnsiConsole.MarkupLine("[bold aqua]Create New Microservice Wizard[/]\n");

            // Step 1: Gather user input
            var projectName = AnsiConsole.Ask<string>(
                "Enter the [green]name of your Microservice[/] (e.g. MyService):");

            var solutionDirectory = AnsiConsole.Ask<string>(
                "Enter the [green]solution directory[/] (default = current directory):",
                Directory.GetCurrentDirectory());

            // Step 2: Choose DB provider
            var dbProvider = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Select a DB provider[/]:")
                    .AddChoices(new[] { "postgresql", "sqlserver", "oracle" })
            );

            // Step 3: Additional features
            var enableDocker = AnsiConsole.Confirm("[green]Include Dockerfile (and docker-compose)?[/]", true);
            var enableTests = AnsiConsole.Confirm("[green]Include a test project?[/]", true);
            var enableSerilog = AnsiConsole.Confirm("[green]Use Serilog for logging?[/]", true);

            // Step 4: Summarize user choices in a nice table
            var summaryTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[yellow]Setting[/]")
                .AddColumn("[yellow]Value[/]");

            summaryTable.AddRow("Microservice Name", projectName);
            summaryTable.AddRow("Target Directory", solutionDirectory);
            summaryTable.AddRow("DB Provider", dbProvider);
            summaryTable.AddRow("Docker", enableDocker.ToString());
            summaryTable.AddRow("Tests", enableTests.ToString());
            summaryTable.AddRow("Serilog", enableSerilog.ToString());

            AnsiConsole.Write(summaryTable);

            // Step 5: Run dotnet new to scaffold
            var targetPath = Path.Combine(solutionDirectory, projectName);
            Directory.CreateDirectory(targetPath);

            var arguments = $"new bogms --output \"{targetPath}\" --dbProvider {dbProvider} --name {projectName} --force";

            AnsiConsole.MarkupLine($"\n[dim]Running command:[/] [yellow]{arguments}[/]");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc.WaitForExit();

            // Step 6: Post-process. E.g. add Docker, tests, Serilog, etc.
            if (enableDocker)
            {
                EnableDockerSupport(targetPath);
            }
            if (enableTests)
            {
                AddTestProject(targetPath, projectName);
            }
            if (enableSerilog)
            {
                EnsureSerilogConfiguration(targetPath);
            }

            AnsiConsole.MarkupLine("\n[bold green]Scaffolding completed successfully![/]");
            AnsiConsole.MarkupLine("[grey]Press any key to return to main menu...[/]");
            Console.ReadKey(true);

            // Re-print banner before returning to main menu
            AnsiConsole.Clear();
            AsciiArt.PrintBanner();
        }

        private static void ShowAboutScreen()
        {
            AnsiConsole.Clear();
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
            panelText.Padding = new Padding(1,1);

            AnsiConsole.Write(panelText);

            AnsiConsole.MarkupLine("[grey]Press any key to return to the main menu...[/]");
            Console.ReadKey(true);

            // Clear and re-print banner
            AnsiConsole.Clear();
            AsciiArt.PrintBanner();
        }

        private static void EnableDockerSupport(string targetPath)
        {
            var dockerFilePath = Path.Combine(targetPath, "Dockerfile");
            if (!File.Exists(dockerFilePath))
            {
                File.WriteAllText(dockerFilePath, @"FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 80
COPY . .
ENTRYPOINT [""dotnet"", ""BOG.MyMicroservice.API.dll""]");
            }

            var composePath = Path.Combine(targetPath, "docker-compose.yml");
            if (!File.Exists(composePath))
            {
                File.WriteAllText(composePath, @"version: '3.8'
services:
  bogmymicroservice:
    build: .
    ports:
      - ""8080:80""
    depends_on:
      - db
  db:
    image: postgres:15-alpine
    environment:
      POSTGRES_USER: user
      POSTGRES_PASSWORD: password
    ports:
      - ""5432:5432""");
            }
        }

        private static void AddTestProject(string targetPath, string projectName)
        {
            var testDir = Path.Combine(targetPath, $"{projectName}.Tests");
            Directory.CreateDirectory(testDir);

            var testProjPath = Path.Combine(testDir, $"{projectName}.Tests.csproj");
            if (!File.Exists(testProjPath))
            {
                File.WriteAllText(testProjPath, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""MSTest.TestAdapter"" Version=""3.0.0"" />
    <PackageReference Include=""MSTest.TestFramework"" Version=""3.0.0"" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include=""..\\BOG.MyMicroservice.API\\BOG.MyMicroservice.API.csproj"" />
    <ProjectReference Include=""..\\BOG.MyMicroservice.Application\\BOG.MyMicroservice.Application.csproj"" />
    <ProjectReference Include=""..\\BOG.MyMicroservice.Domain\\BOG.MyMicroservice.Domain.csproj"" />
    <ProjectReference Include=""..\\BOG.MyMicroservice.Infrastructure\\BOG.MyMicroservice.Infrastructure.csproj"" />
  </ItemGroup>
</Project>");
            }

            var testExample = Path.Combine(testDir, "SampleTests.cs");
            if (!File.Exists(testExample))
            {
                File.WriteAllText(testExample, @"using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace " + projectName + @".Tests
{
    [TestClass]
    public class SampleTests
    {
        [TestMethod]
        public void BasicTest()
        {
            Assert.AreEqual(2, 1+1);
        }
    }
}");
            }
        }

        private static void EnsureSerilogConfiguration(string targetPath)
        {
            // Placeholder for adding or adjusting Serilog config if needed
        }
    }
}
