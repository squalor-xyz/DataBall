using squalor.DataBall;

class Program
{
    static async Task Main(string[] args)
    {
        var db = new DataBall("config.json"); // Optional config

        // Example usage
        db.AddColumn<string>("Name", new[] { "Alice", "Bob" });
        db.AddColumn<int>("Age", new[] { 30, 25 });
        db.AddRow(new object[] { "Alice", 30 });
        db.AddRow(new object[] { "Bob", 25 });

        db.Bounce();
        db.Roll(ExportType.Csv, "output.csv");

        // Import example
        db.ImportFromCsv("input.csv", append: true);

        Console.WriteLine("DataBall demo completed.");
    }
}