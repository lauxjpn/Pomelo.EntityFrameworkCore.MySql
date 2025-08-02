namespace RandomSchemaCreator;

internal static class Program
{
    private static void Main(string[] args)
    {
        var databaseName = args[0];
        var tableCount = int.Parse(args[1]);
        var columnCount = int.Parse(args[2]);
        var foreignKeyCount = int.Parse(args[3]);
        var rngSeed = args.Length > 4
            ? int.Parse(args[4])
            : 42;

        var sqlScript = new SchemaGenerator(
                tableCount,
                columnCount,
                foreignKeyCount)
            .Run(databaseName, rngSeed);

        Console.WriteLine(sqlScript);
    }
}
