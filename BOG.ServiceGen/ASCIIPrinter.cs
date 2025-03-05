using Spectre.Console;

namespace BOG.Microservice.Scaffolder
{
    public static class AsciiArt
    {
        public static void PrintBanner()
        {
            AnsiConsole.MarkupLine("[orange1]Bank of Georgia  [/]");
            AnsiConsole.WriteLine();
            
            AnsiConsole.Write(
                new FigletText("BOG Microservice Builder")
                    .Centered()
                    .Color(Color.Orange1));
            
            AnsiConsole.WriteLine();
        }
    }
}