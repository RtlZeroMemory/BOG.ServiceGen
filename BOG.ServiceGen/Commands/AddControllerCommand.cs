using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace BOG.ServiceGen.Commands
{
    /// <summary>
    /// Settings for "bogms add-controller".
    /// </summary>
    public class AddControllerSettings : CommandSettings
    {
        [CommandOption("-p|--path <PROJPATH>")]
        [Description("Path to the root of the microservice solution (where the .sln resides)")]
        [DefaultValue("")]
        public string ProjectRoot { get; set; } = Directory.GetCurrentDirectory();

        [CommandOption("-e|--entities <ENTITIES>")]
        [Description("Comma-separated list of entity names (e.g. Customer,Order)")]
        public string EntityNames { get; set; } = null!;

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ProjectRoot))
                return ValidationResult.Error("ProjectRoot cannot be empty.");

            if (string.IsNullOrWhiteSpace(EntityNames))
                return ValidationResult.Error("You must specify at least one entity (e.g. --entities Customer,Order)");

            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Implements "bogms add-controller" logic:
    ///   • For each entity, create a <EntityName>sController.cs under API\Controllers\
    ///   • Uses I<EntityName>Service injection
    ///   • Scaffold basic CRUD endpoints
    /// </summary>
    public class AddControllerCommand : Command<AddControllerSettings>
    {
        public override int Execute(CommandContext context, AddControllerSettings settings)
        {
            var root = settings.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar);
            var projectName = Path.GetFileName(root);

            if (string.IsNullOrWhiteSpace(projectName))
            {
                AnsiConsole.MarkupLine("[red]ERROR:[/] Could not determine project name from path.");
                return 1;
            }

            var entities = settings.EntityNames
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToList();

            if (!entities.Any())
            {
                AnsiConsole.MarkupLine("[red]ERROR:[/] No valid entity names provided.");
                return 1;
            }

            try
            {
                foreach (var entity in entities)
                {
                    GenerateController(root, projectName, entity);
                }
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// Writes a REST controller stub for <entityName> under src\<ProjectName>.API\Controllers\.
        /// </summary>
        private void GenerateController(string root, string projectName, string entityName)
        {
            var controllersFolder = Path.Combine(root, "src", $"{projectName}.API", "Controllers");
            Directory.CreateDirectory(controllersFolder);

            var controllerFile = Path.Combine(controllersFolder, $"{entityName}sController.cs");
            if (File.Exists(controllerFile))
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping:[/] Controller for '{entityName}' already exists.");
                return;
            }

            var source = BuildControllerSource(projectName, entityName);
            File.WriteAllText(controllerFile, source);
            AnsiConsole.MarkupLine($"[green]Generated controller:[/] {Path.GetRelativePath(root, controllerFile)}");
        }

        /// <summary>
        /// Returns the source code for "<EntityName>sController.cs".
        /// </summary>
        private string BuildControllerSource(string projectName, string entityName)
        {
            // We assume service interface: I<EntityName>Service
            // and that it is registered in DI in Program.cs
            var serviceInterface = $"I{entityName}Service";
            var serviceField = $"_{char.ToLowerInvariant(entityName[0])}{entityName.Substring(1)}Service";

            return $@"using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using {projectName}.Application.Services;
using {projectName}.Domain.Entities;

namespace {projectName}.API.Controllers
{{
    [ApiController]
    [Route(""api/[controller]"")]
    public class {entityName}sController : ControllerBase
    {{
        private readonly {serviceInterface} {serviceField};

        public {entityName}sController({serviceInterface} {serviceField.Substring(1)})
        {{
            {serviceField} = {serviceField.Substring(1)};
        }}

        [HttpGet]
        public async Task<ActionResult<IEnumerable<{entityName}>>> GetAll()
        {{
            var items = await {serviceField}.GetAllAsync();
            return Ok(items);
        }}

        [HttpGet(""{{
id}}"")]
        public async Task<ActionResult<{entityName}>> GetById(int id)
        {{
            var item = await {serviceField}.GetByIdAsync(id);
            if (item == null) return NotFound();
            return Ok(item);
        }}

        [HttpPost]
        public async Task<ActionResult<{entityName}>> Create({entityName} entity)
        {{
            var created = await {serviceField}.CreateAsync(entity);
            return CreatedAtAction(nameof(GetById), new {{ id = created.Id }}, created);
        }}

        [HttpPut(""{{
id}}"")]
        public async Task<IActionResult> Update(int id, {entityName} entity)
        {{
            var updated = await {serviceField}.UpdateAsync(id, entity);
            if (updated == null) return NotFound();
            return NoContent();
        }}

        [HttpDelete(""{{
id}}"")]
        public async Task<IActionResult> Delete(int id)
        {{
            var success = await {serviceField}.DeleteAsync(id);
            if (!success) return NotFound();
            return NoContent();
        }}
    }}
}}";
        }
    }
}
