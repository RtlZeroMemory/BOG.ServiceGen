// File: BOG.ServiceGen/Helpers/ScaffoldingHelpers.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Spectre.Console;

namespace BOG.ServiceGen.Helpers
{
    public static class ScaffoldingHelpers
    {
        /// <summary>
        /// Scans the Domain project for any classes decorated with [GenerateEntityService],
        /// generates a "<EntityName>Service.generated.cs" in the Application project,
        /// and creates a "<EntityName>Controller.generated.cs" in the API project.
        /// It tracks already‐generated entities in a small manifest JSON so that only brand‐new entities
        /// get output on subsequent runs.
        /// </summary>
        public static async Task GenerateEntityServicesIncrementally(
            string targetPath,
            string projectName,
            string dbProvider)
        {
            //
            // STEP A: Locate & open the Domain .csproj with Roslyn
            //
            var domainProjPath = Path.Combine(
                targetPath,
                "src",
                $"{projectName}.Domain",
                $"{projectName}.Domain.csproj"
            );

            if (!File.Exists(domainProjPath))
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] Could not locate Domain project at {domainProjPath}");
                return;
            }

            // Register MSBuild so RoslynWorkspace can load it
            MSBuildLocator.RegisterDefaults();
            using var workspace = MSBuildWorkspace.Create();
            AnsiConsole.MarkupLine("[grey]Opening Domain project with Roslyn (MSBuildWorkspace)…[/]");
            var domainProj  = await workspace.OpenProjectAsync(domainProjPath);
            var compilation = await domainProj.GetCompilationAsync()!;
            AnsiConsole.MarkupLine("[green]Domain project loaded into Roslyn.[/]");

            //
            // STEP B: Find the GenerateEntityServiceAttribute symbol
            //
            var crudAttrFullName = $"{projectName}.Domain.Attributes.GenerateEntityServiceAttribute";
            var crudAttrSymbol   = compilation.GetTypeByMetadataName(crudAttrFullName);

            if (crudAttrSymbol == null)
            {
                Console.WriteLine("DEBUG: Could not find GenerateEntityServiceAttribute by metadata name. Doing fallback scan…");
                crudAttrSymbol = FindGenerateEntityAttributeFallback(compilation);
                if (crudAttrSymbol != null)
                {
                    crudAttrFullName = crudAttrSymbol.ToDisplayString();
                    AnsiConsole.MarkupLine($"[green]DEBUG: Found attribute via fallback scan:[/] [yellow]{crudAttrFullName}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]WARNING:[/] Could not find ‘GenerateEntityServiceAttribute’ in Domain.  No services/controllers will be generated.");
                    return;
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]DEBUG: Found attribute metadata:[/] [yellow]{crudAttrFullName}[/]");
            }

            //
            // STEP C: Enumerate all types in Domain, list their attributes
            //
            var allTypes = GetAllTypesRecursive(compilation.GlobalNamespace)
                           .OfType<INamedTypeSymbol>()
                           .ToList();

            Console.WriteLine("DEBUG: Dumping all types and their attributes:");
            foreach (var typeSym in allTypes)
            {
                var fullName = typeSym.ToDisplayString();
                Console.WriteLine($"  Type: {fullName}");
                var attrs = typeSym.GetAttributes();
                if (attrs.Length == 0)
                {
                    Console.WriteLine("    (no attributes)");
                }
                else
                {
                    foreach (var ad in attrs)
                    {
                        var attrName = ad.AttributeClass?.ToDisplayString() ?? "[unknown]";
                        Console.WriteLine($"    Attribute: {attrName}");
                    }
                }
            }
            Console.WriteLine("");

            //
            // STEP D: Filter only those types decorated with [GenerateEntityService]
            //
            var entities = allTypes
                .Where(ts =>
                    ts.GetAttributes().Any(ad =>
                        ad.AttributeClass != null
                        && ad.AttributeClass.ToDisplayString().Equals(
                            crudAttrFullName,
                            StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (entities.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No classes with [[GenerateEntityService]][/] found.  Skipping generation.");
                return;
            }
            else
            {
                AnsiConsole.MarkupLine($"[grey]DEBUG: {entities.Count} classes found with [[GenerateEntityService]][/]");
            }

            //
            // STEP E: Load or Create the Manifest JSON under Application\Services\_EntityManifest.json
            //
            var manifestPath = Path.Combine(
                targetPath,
                "src",
                $"{projectName}.Application",
                "Services",
                "_EntityManifest.json"
            );

            EntityManifest manifest;
            if (File.Exists(manifestPath))
            {
                var existingJson = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<EntityManifest>(existingJson) ?? new EntityManifest();
            }
            else
            {
                manifest = new EntityManifest();
            }

            var knownNames = new HashSet<string>(manifest.Entities.Select(e => e.Name));

            //
            // STEP F: Generate Service & Controller for each newly discovered entity
            //
            var servicesRoot = Path.Combine(
                targetPath,
                "src",
                $"{projectName}.Application",
                "Services"
            );
            Directory.CreateDirectory(servicesRoot);

            var controllersRoot = Path.Combine(
                targetPath,
                "src",
                $"{projectName}.API",
                "Controllers"
            );
            Directory.CreateDirectory(controllersRoot);

            var newlyAddedEntries = new List<ManifestEntry>();
            foreach (var entitySym in entities)
            {
                var entityName = entitySym.Name;
                if (knownNames.Contains(entityName))
                    continue; // Already in manifest → skip

                AnsiConsole.MarkupLine($"[grey]DEBUG: Found new entity:[/] [yellow]{entityName}[/]");

                // 1) Determine the entity’s actual namespace (e.g. “MySvc.Domain.Entities”)
                var entityNamespace = entitySym.ContainingNamespace.ToDisplayString();

                // 2) Detect the Id property’s type (int, Guid, string, decimal, etc.)
                var idProp = entitySym.GetMembers()
                                      .OfType<IPropertySymbol>()
                                      .FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));
                var idTypeName = "int";
                if (idProp != null)
                {
                    idTypeName = idProp.Type.ToDisplayString();
                }

                //
                // ── Generate the Service file ───────────────────────────────────────────────────────────
                //
                var serviceFolder = Path.Combine(servicesRoot, entityName);
                Directory.CreateDirectory(serviceFolder);

                var serviceFilePath = Path.Combine(serviceFolder, $"{entityName}Service.generated.cs");
                var serviceCode     = BuildEntityServiceSource(projectName, entityName, entityNamespace, idTypeName);
                File.WriteAllText(serviceFilePath, serviceCode);
                AnsiConsole.MarkupLine($"[green]Generated service:[/] {Path.GetRelativePath(targetPath, serviceFilePath)}");

                //
                // ── Generate the Controller file ───────────────────────────────────────────────────────
                //
                var controllerFilePath = Path.Combine(controllersRoot, $"{entityName}Controller.generated.cs");
                var controllerCode     = BuildControllerSource(projectName, entityName, entityNamespace, idTypeName);
                File.WriteAllText(controllerFilePath, controllerCode);
                AnsiConsole.MarkupLine($"[green]Generated controller:[/] {Path.GetRelativePath(targetPath, controllerFilePath)}");

                newlyAddedEntries.Add(new ManifestEntry
                {
                    Name              = entityName,
                    IncludeSoftDelete = false,
                    GeneratedOn       = DateTime.UtcNow
                });
            }

            //
            // STEP G: Update the manifest if any new entities were added
            //
            if (newlyAddedEntries.Any())
            {
                manifest.Entities.AddRange(newlyAddedEntries);
                var opts   = new JsonSerializerOptions { WriteIndented = true };
                var newJson = JsonSerializer.Serialize(manifest, opts);
                File.WriteAllText(manifestPath, newJson);
                AnsiConsole.MarkupLine($"[green]Updated manifest:[/] {Path.GetRelativePath(targetPath, manifestPath)}");
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]No new entities found.  Manifest is up-to-date.[/]");
            }

            //
            // STEP H: Inject <Compile Include="Services\**\*.generated.cs" /> into Application .csproj
            //
            var appProjPath = Path.Combine(
                targetPath,
                "src",
                $"{projectName}.Application",
                $"{projectName}.Application.csproj"
            );
            if (File.Exists(appProjPath))
            {
                var appProjText = File.ReadAllText(appProjPath);
                const string compileInclude = @"<Compile Include=""Services\**\*.generated.cs"" />";
                if (!appProjText.Contains(compileInclude))
                {
                    // Insert inside the first <ItemGroup> so it lives alongside existing ProjectReferences
                    var marker = "<ItemGroup>";
                    var idx    = appProjText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        idx += marker.Length;
                        var inject = $@"
    {compileInclude}";
                        appProjText = appProjText.Insert(idx, inject);
                        File.WriteAllText(appProjPath, appProjText);
                        AnsiConsole.MarkupLine($"[green]Updated:[/] {Path.GetRelativePath(targetPath, appProjPath)} to include generated services");
                    }
                    else
                    {
                        // If there wasn’t any <ItemGroup>, append a new one before </Project>
                        var injectGroup = $@"
  <ItemGroup>
    {compileInclude}
  </ItemGroup>";
                        var closing = "</Project>";
                        var closeIdx = appProjText.LastIndexOf(closing, StringComparison.OrdinalIgnoreCase);
                        if (closeIdx >= 0)
                        {
                            appProjText = appProjText.Insert(closeIdx, injectGroup);
                            File.WriteAllText(appProjPath, appProjText);
                            AnsiConsole.MarkupLine($"[green]Appended new ItemGroup in:[/] {Path.GetRelativePath(targetPath, appProjPath)}");
                        }
                    }
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] Could not find {projectName}.Application.csproj to inject generated services.");
            }
        }

        /// <summary>
        /// Builds the "<EntityName>Service.generated.cs" source file.  This includes:
        ///  • "using {entityNamespace};" so that the domain type is resolved,
        ///  • the correct Id‐type (int, string, Guid, etc.) in method signatures,
        ///  • an ILogger injected into the service constructor,
        ///  • a set of basic CRUD methods inside "#region <auto-generated>…</auto-generated>".
        /// </summary>
        private static string BuildEntityServiceSource(
            string projectName,
            string entityName,
            string entityNamespace,
            string idType)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using Microsoft.EntityFrameworkCore;");
            sb.AppendLine("using Microsoft.Extensions.Logging;");
            sb.AppendLine($"using {entityNamespace};");
            sb.AppendLine($"using {projectName}.Infrastructure;");
            sb.AppendLine();
            sb.AppendLine($"namespace {projectName}.Application.Services");
            sb.AppendLine("{");
            // Interface
            sb.AppendLine($"    public interface I{entityName}Service");
            sb.AppendLine("    {");
            sb.AppendLine($"        Task<IEnumerable<{entityName}>> GetAllAsync();");
            sb.AppendLine($"        Task<{entityName}?> GetByIdAsync({idType} id);");
            sb.AppendLine($"        Task<{entityName}> CreateAsync({entityName} entity);");
            sb.AppendLine($"        Task<{entityName}?> UpdateAsync({idType} id, {entityName} updatedEntity);");
            sb.AppendLine($"        Task<bool> DeleteAsync({idType} id);");
            sb.AppendLine("    }");
            sb.AppendLine();
            // Implementation
            sb.AppendLine($"    public partial class {entityName}Service : I{entityName}Service");
            sb.AppendLine("    {");
            sb.AppendLine("        private readonly AppDbContext _dbContext;");
            sb.AppendLine($"        private readonly ILogger<{entityName}Service> _logger;");
            sb.AppendLine();
            sb.AppendLine($"        public {entityName}Service(AppDbContext dbContext, ILogger<{entityName}Service> logger)");
            sb.AppendLine("        {");
            sb.AppendLine("            _dbContext = dbContext;");
            sb.AppendLine("            _logger = logger;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        #region <auto-generated> CRUD Methods (do not modify inside this region) </auto-generated>");
            sb.AppendLine();
            // GetAllAsync
            sb.AppendLine($"        public async Task<IEnumerable<{entityName}>> GetAllAsync()");
            sb.AppendLine("        {");
            sb.AppendLine($"            return await _dbContext.Set<{entityName}>().ToListAsync();");
            sb.AppendLine("        }");
            sb.AppendLine();
            // GetByIdAsync
            sb.AppendLine($"        public async Task<{entityName}?> GetByIdAsync({idType} id)");
            sb.AppendLine("        {");
            sb.AppendLine($"            return await _dbContext.Set<{entityName}>().FindAsync(id);");
            sb.AppendLine("        }");
            sb.AppendLine();
            // CreateAsync
            sb.AppendLine($"        public async Task<{entityName}> CreateAsync({entityName} entity)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (entity == null) throw new ArgumentNullException(nameof(entity));");
            sb.AppendLine($"            _dbContext.Set<{entityName}>().Add(entity);");
            sb.AppendLine("            await _dbContext.SaveChangesAsync();");
            sb.AppendLine($"            _logger.LogInformation(\"Created new {entityName} with Id {{Id}}\", entity.Id);");
            sb.AppendLine("            return entity;");
            sb.AppendLine("        }");
            sb.AppendLine();
            // UpdateAsync
            sb.AppendLine($"        public async Task<{entityName}?> UpdateAsync({idType} id, {entityName} updatedEntity)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var existing = await _dbContext.Set<{entityName}>().FindAsync(id);");
            sb.AppendLine("            if (existing == null) return null;");
            sb.AppendLine();
            sb.AppendLine($"            foreach (var prop in typeof({entityName}).GetProperties().Where(p => p.Name != \"Id\"))");
            sb.AppendLine("                prop.SetValue(existing, prop.GetValue(updatedEntity));");
            sb.AppendLine();
            sb.AppendLine("            await _dbContext.SaveChangesAsync();");
            sb.AppendLine($"            _logger.LogInformation(\"Updated {entityName} with Id {{Id}}\", id);");
            sb.AppendLine("            return existing;");
            sb.AppendLine("        }");
            sb.AppendLine();
            // DeleteAsync
            sb.AppendLine($"        public async Task<bool> DeleteAsync({idType} id)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var entity = await _dbContext.Set<{entityName}>().FindAsync(id);");
            sb.AppendLine("            if (entity == null) return false;");
            sb.AppendLine($"            _dbContext.Set<{entityName}>().Remove(entity);");
            sb.AppendLine("            await _dbContext.SaveChangesAsync();");
            sb.AppendLine($"            _logger.LogInformation(\"Deleted {entityName} with Id {{Id}}\", id);");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        #endregion");
            sb.AppendLine();
            sb.AppendLine("        // You can add custom methods below this line.  They will not be overwritten.");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the "<EntityName>Controller.generated.cs" source, including:
        ///  • [ApiController], [Route("api/[controller]")]
        ///  • GET (all), GET (by id), POST, PUT, DELETE
        ///  • [ProducesResponseType(...)] attributes
        ///  • Calls into I{EntityName}Service from Application
        ///  • Uses the correct Id type (int, string, Guid, decimal, etc.)
        /// </summary>
        private static string BuildControllerSource(
            string projectName,
            string entityName,
            string entityNamespace,
            string idType)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using Microsoft.AspNetCore.Mvc;");
            sb.AppendLine($"using {projectName}.Application.Services;");
            sb.AppendLine($"using {entityNamespace};");
            sb.AppendLine();
            sb.AppendLine($"namespace {projectName}.API.Controllers");
            sb.AppendLine("{");
            sb.AppendLine("    [ApiController]");
            sb.AppendLine($"    [Route(\"api/[controller]\")]");
            sb.AppendLine($"    public class {entityName}Controller : ControllerBase");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly I{entityName}Service _service;");
            sb.AppendLine();
            sb.AppendLine($"        public {entityName}Controller(I{entityName}Service service)");
            sb.AppendLine("        {");
            sb.AppendLine("            _service = service;");
            sb.AppendLine("        }");
            sb.AppendLine();
            // GET all
            sb.AppendLine("        /// <summary>");
            sb.AppendLine($"        /// GET all {entityName} items");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        [HttpGet]");
            sb.AppendLine($"        [ProducesResponseType(typeof(IEnumerable<{entityName}>), 200)]");
            sb.AppendLine($"        public async Task<ActionResult<IEnumerable<{entityName}>>> GetAll()");
            sb.AppendLine("        {");
            sb.AppendLine($"            var list = await _service.GetAllAsync();");
            sb.AppendLine("            return Ok(list);");
            sb.AppendLine("        }");
            sb.AppendLine();
            // GET by ID
            sb.AppendLine("        /// <summary>");
            sb.AppendLine($"        /// GET a single {entityName} by ID");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine($"        [HttpGet(\"{{id}}\")]");
            sb.AppendLine($"        [ProducesResponseType(typeof({entityName}), 200)]");
            sb.AppendLine("        [ProducesResponseType(404)]");
            sb.AppendLine($"        public async Task<ActionResult<{entityName}>> GetById({idType} id)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var item = await _service.GetByIdAsync(id);");
            sb.AppendLine("            if (item == null) return NotFound();");
            sb.AppendLine("            return Ok(item);");
            sb.AppendLine("        }");
            sb.AppendLine();
            // POST
            sb.AppendLine("        /// <summary>");
            sb.AppendLine($"        /// CREATE a new {entityName}");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        [HttpPost]");
            sb.AppendLine($"        [ProducesResponseType(typeof({entityName}), 201)]");
            sb.AppendLine($"        public async Task<ActionResult<{entityName}>> Create({entityName} entity)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var created = await _service.CreateAsync(entity);");
            sb.AppendLine($"            return CreatedAtAction(nameof(GetById), new {{ id = created.Id }}, created);");
            sb.AppendLine("        }");
            sb.AppendLine();
            // PUT
            sb.AppendLine("        /// <summary>");
            sb.AppendLine($"        /// UPDATE an existing {entityName}");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine($"        [HttpPut(\"{{id}}\")]");
            sb.AppendLine($"        [ProducesResponseType(typeof({entityName}), 200)]");
            sb.AppendLine("        [ProducesResponseType(404)]");
            sb.AppendLine($"        public async Task<ActionResult<{entityName}>> Update({idType} id, {entityName} entity)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var updated = await _service.UpdateAsync(id, entity);");
            sb.AppendLine("            if (updated == null) return NotFound();");
            sb.AppendLine("            return Ok(updated);");
            sb.AppendLine("        }");
            sb.AppendLine();
            // DELETE
            sb.AppendLine("        /// <summary>");
            sb.AppendLine($"        /// DELETE a {entityName} by ID");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine($"        [HttpDelete(\"{{id}}\")]");
            sb.AppendLine("        [ProducesResponseType(204)]");
            sb.AppendLine("        [ProducesResponseType(404)]");
            sb.AppendLine($"        public async Task<IActionResult> Delete({idType} id)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var success = await _service.DeleteAsync(id);");
            sb.AppendLine("            if (!success) return NotFound();");
            sb.AppendLine("            return NoContent();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Fallback to locate "GenerateEntityServiceAttribute" if metadata lookup fails.
        /// </summary>
        private static INamedTypeSymbol? FindGenerateEntityAttributeFallback(Compilation compilation)
        {
            foreach (var ns in compilation.GlobalNamespace.GetNamespaceMembers())
            {
                var found = ScanNamespaceForAttribute(ns, "GenerateEntityServiceAttribute");
                if (found != null) return found;
            }
            return null;
        }

        private static INamedTypeSymbol? ScanNamespaceForAttribute(INamespaceSymbol ns, string attributeName)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol nestedNs)
                {
                    var found = ScanNamespaceForAttribute(nestedNs, attributeName);
                    if (found != null) return found;
                }
                else if (member is INamedTypeSymbol nt && nt.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
                {
                    return nt;
                }
            }
            return null;
        }

        /// <summary>
        /// Recursively walk a namespace symbol to return all nested named-type symbols.
        /// </summary>
        private static IEnumerable<ISymbol> GetAllTypesRecursive(ISymbol symbol)
        {
            if (symbol is INamespaceSymbol ns)
            {
                foreach (var member in ns.GetMembers())
                {
                    if (member is INamespaceSymbol nestedNs)
                    {
                        foreach (var t in GetAllTypesRecursive(nestedNs))
                            yield return t;
                    }
                    else if (member is INamedTypeSymbol nt)
                    {
                        yield return nt;
                        foreach (var child in nt.GetTypeMembers().Cast<ISymbol>())
                            yield return child;
                    }
                }
            }
        }

        /// <summary>
        /// Removes (inclusive) everything between startTag and endTag, if both exist. Otherwise returns original source.
        /// </summary>
        public static string RemoveBetweenTags(string source, string startTag, string endTag)
        {
            var si = source.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (si < 0) return source;
            var ei = source.IndexOf(endTag, si, StringComparison.OrdinalIgnoreCase);
            if (ei < 0) return source;
            ei += endTag.Length;
            return source.Remove(si, ei - si);
        }

        //
        // ── Classic Helper Methods: the ones that NewServiceCommand expects ─────────────────────────
        //

        /// <summary>
        /// Creates a “Dockerfile” and “docker-compose.yml” in <targetPath> if they do not already exist.
        /// </summary>
        public static void EnableDockerSupport(string targetPath)
        {
            var dockerFilePath = Path.Combine(targetPath, "Dockerfile");
            if (!File.Exists(dockerFilePath))
            {
                File.WriteAllText(dockerFilePath, $@"FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
COPY . .
ENTRYPOINT [""dotnet"", ""{Path.GetFileName(targetPath)}.API.dll""]");
                AnsiConsole.MarkupLine($"[green]Created Dockerfile:[/] {Path.GetRelativePath(targetPath, dockerFilePath)}");
            }

            var composePath = Path.Combine(targetPath, "docker-compose.yml");
            if (!File.Exists(composePath))
            {
                File.WriteAllText(composePath, @"version: '3.8'
services:
  app:
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
                AnsiConsole.MarkupLine($"[green]Created docker-compose.yml:[/] {Path.GetRelativePath(targetPath, composePath)}");
            }
        }

        /// <summary>
        /// Creates a MSTest project under "<targetPath>\<ProjectName>.Tests" with a sample test class.
        /// </summary>
        public static void AddTestProject(string targetPath, string projectName)
        {
            var testDir = Path.Combine(targetPath, $"{projectName}.Tests");
            Directory.CreateDirectory(testDir);

            var testProjPath = Path.Combine(testDir, $"{projectName}.Tests.csproj");
            if (!File.Exists(testProjPath))
            {
                File.WriteAllText(testProjPath, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""MSTest.TestAdapter"" Version=""3.0.0"" />
    <PackageReference Include=""MSTest.TestFramework"" Version=""3.0.0"" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include=""..\{projectName}.API\{projectName}.API.csproj"" />
    <ProjectReference Include=""..\{projectName}.Application\{projectName}.Application.csproj"" />
    <ProjectReference Include=""..\{projectName}.Domain\{projectName}.Domain.csproj"" />
    <ProjectReference Include=""..\{projectName}.Infrastructure\{projectName}.Infrastructure.csproj"" />
  </ItemGroup>
</Project>");
                AnsiConsole.MarkupLine($"[green]Created test .csproj:[/] {Path.GetRelativePath(targetPath, testProjPath)}");
            }

            var sampleTest = Path.Combine(testDir, "SampleTests.cs");
            if (!File.Exists(sampleTest))
            {
                File.WriteAllText(sampleTest, $@"using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace {projectName}.Tests
{{
    [TestClass]
    public class SampleTests
    {{
        [TestMethod]
        public void BasicTest()
        {{
            Assert.AreEqual(2, 1 + 1);
        }}
    }}
}}");
                AnsiConsole.MarkupLine($"[green]Created sample test file:[/] {Path.GetRelativePath(targetPath, sampleTest)}");
            }
        }

        /// <summary>
        /// Placeholder for Serilog configuration.  Currently a no-op with an informational message.
        /// </summary>
        public static void EnsureSerilogConfiguration(string targetPath)
        {
            AnsiConsole.MarkupLine("[grey]Skipping Serilog configuration (no action).[/]");
        }

        /// <summary>
        /// Inserts the appropriate EF Core provider PackageReference into
        /// "<ProjectName>.Infrastructure\<ProjectName>.Infrastructure.csproj".
        /// </summary>
        public static void InjectEfProvider(string targetPath, string projectName, string dbProvider)
        {
            var infraProjPath = Path.Combine(
                targetPath,
                "src",
                $"{projectName}.Infrastructure",
                $"{projectName}.Infrastructure.csproj"
            );

            if (!File.Exists(infraProjPath))
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] Could not locate Infrastructure csproj at {infraProjPath}");
                return;
            }

            var infraXml = File.ReadAllText(infraProjPath);

            // Remove any existing provider block
            infraXml = RemoveBetweenTags(infraXml, "<!-- BEGIN_EF_PROVIDER -->", "<!-- END_EF_PROVIDER -->");

            // Choose the correct EF provider reference
            var pkgRef = dbProvider.ToLowerInvariant() switch
            {
                "sqlserver" => @"<PackageReference Include=""Microsoft.EntityFrameworkCore.SqlServer"" Version=""8.0.0"" />",
                "oracle"    => @"<PackageReference Include=""Oracle.EntityFrameworkCore"" Version=""8.0.0"" />",
                _           => @"<PackageReference Include=""Npgsql.EntityFrameworkCore.PostgreSQL"" Version=""8.0.0"" />",
            };

            var providerBlock = $@"
    <!-- BEGIN_EF_PROVIDER -->
    {pkgRef}
    <!-- END_EF_PROVIDER -->";

            var marker = "<ItemGroup>";
            var idx = infraXml.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                idx += marker.Length;
                infraXml = infraXml.Insert(idx, providerBlock);
                File.WriteAllText(infraProjPath, infraXml);
                AnsiConsole.MarkupLine($"[green]Injected EF provider:[/] {Path.GetRelativePath(targetPath, infraProjPath)}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]WARNING:[/] Could not find <ItemGroup> in {infraProjPath}. Skipping EF provider injection.");
            }
        }

        /// <summary>
        /// Inserts AddDbContext&lt;AppDbContext&gt;(…) lines into
        /// "<ProjectName>.API\Program.cs".  Four possible insertion points:
        ///  1) Before "// Add Serilog"
        ///  2) After "var builder = WebApplication.CreateBuilder"
        ///  3) Before "var app = builder.Build()"
        ///  4) Append at end of file if none of the above exist.
        /// </summary>
        public static void InjectEfRegistration(string targetPath, string projectName, string dbProvider)
        {
            var apiProgramPath = Path.Combine(
                targetPath,
                "src",
                $"{projectName}.API",
                "Program.cs"
            );

            if (!File.Exists(apiProgramPath))
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] Could not locate API Program.cs at {apiProgramPath}");
                return;
            }

            var apiText = File.ReadAllText(apiProgramPath);

            // Remove any existing /*<EF_REGISTRATION>*/…/*</EF_REGISTRATION>*/ block
            apiText = RemoveBetweenTags(apiText, "/*<EF_REGISTRATION>*/", "/*</EF_REGISTRATION>*/");

            // Build the EF snippet
            var efSnippet = dbProvider.ToLowerInvariant() switch
            {
                "sqlserver" => @"
    /*<EF_REGISTRATION>*/
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseSqlServer(builder.Configuration.GetConnectionString(""SqlServer"")));
    /*</EF_REGISTRATION>*/",
                "oracle" => @"
    /*<EF_REGISTRATION>*/
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseOracle(builder.Configuration.GetConnectionString(""Oracle"")));
    /*</EF_REGISTRATION>*/",
                _ => @"
    /*<EF_REGISTRATION>*/
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseNpgsql(builder.Configuration.GetConnectionString(""PostgreSQL"")));
    /*</EF_REGISTRATION>*/"
            };

            // 1) Try to insert just before "// Add Serilog"
            var serilogMarker = "// Add Serilog";
            var serilogIdx    = apiText.IndexOf(serilogMarker, StringComparison.OrdinalIgnoreCase);
            if (serilogIdx >= 0)
            {
                apiText = apiText.Insert(serilogIdx, efSnippet + Environment.NewLine);
                File.WriteAllText(apiProgramPath, apiText);
                AnsiConsole.MarkupLine($"[green]Injected EF registration before “// Add Serilog”:[/] {Path.GetRelativePath(targetPath, apiProgramPath)}");
                return;
            }

            // 2) If not found, look for "var builder = WebApplication.CreateBuilder"
            var builderMarker = "var builder = WebApplication.CreateBuilder";
            var builderIdx    = apiText.IndexOf(builderMarker, StringComparison.OrdinalIgnoreCase);
            if (builderIdx >= 0)
            {
                var lineEnd = apiText.IndexOf(Environment.NewLine, builderIdx, StringComparison.OrdinalIgnoreCase);
                if (lineEnd < 0) lineEnd = builderIdx + builderMarker.Length;
                else            lineEnd += Environment.NewLine.Length;

                apiText = apiText.Insert(lineEnd, efSnippet + Environment.NewLine);
                File.WriteAllText(apiProgramPath, apiText);
                AnsiConsole.MarkupLine($"[green]Injected EF registration after “var builder = WebApplication.CreateBuilder…”:[/] {Path.GetRelativePath(targetPath, apiProgramPath)}");
                return;
            }

            // 3) If still not found, insert before "var app = builder.Build()"
            var appBuildMarker = "var app = builder.Build";
            var appIdx = apiText.IndexOf(appBuildMarker, StringComparison.OrdinalIgnoreCase);
            if (appIdx >= 0)
            {
                apiText = apiText.Insert(appIdx, efSnippet + Environment.NewLine);
                File.WriteAllText(apiProgramPath, apiText);
                AnsiConsole.MarkupLine($"[green]Injected EF registration before “var app = builder.Build()”:[/] {Path.GetRelativePath(targetPath, apiProgramPath)}");
                return;
            }

            // 4) Fallback: append at the very end of Program.cs
            apiText += Environment.NewLine + efSnippet + Environment.NewLine;
            File.WriteAllText(apiProgramPath, apiText);
            AnsiConsole.MarkupLine($"[yellow]WARNING:[/] Could not find any known insertion point in Program.cs.  EF registration appended to end of file.");
        }
        
        #region Manifest Data Structures

        private class ManifestEntry
        {
            public string Name { get; set; } = null!;
            public bool IncludeSoftDelete { get; set; }
            public DateTime GeneratedOn { get; set; }
        }

        private class EntityManifest
        {
            public List<ManifestEntry> Entities { get; set; } = new();
        }

        #endregion
    }
}
