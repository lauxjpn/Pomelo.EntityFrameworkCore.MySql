using System.Text;

namespace RandomSchemaCreator;

public class SchemaGenerator
{
    private static readonly string[] _columnTypes =
    [
        "INT",
        "VARCHAR(255)",
        "DATETIME(6)",
    ];

    private readonly int _tableCount;
    private readonly int _columnCount;
    private readonly int _foreignKeyCount;

    public SchemaGenerator(
        int tableCount,
        int columnCount,
        int foreignKeyCount)
    {
        _tableCount = tableCount;
        _columnCount = columnCount;
        _foreignKeyCount = foreignKeyCount;
    }

    public string Run(string databaseName, int rngSeed)
    {
        var rand = new Random(rngSeed);
        var sql = new StringBuilder();

        sql.AppendLine($"CREATE DATABASE IF NOT EXISTS `{databaseName}`;")
            .AppendLine($"USE `{databaseName}`;");

        var tableNames = Enumerable.Range(0, _tableCount)
            .Select(n => $"table{n}")
            .ToArray();

        var constraints = new StringBuilder();

        foreach (var tableName in tableNames)
        {
            var columns = Enumerable.Range(0, _columnCount)
                .Select(GetColumnDefinition)
                .Prepend("`id` INT PRIMARY KEY AUTO_INCREMENT");

            for (var foreignKeyIndex = 0; foreignKeyIndex < _foreignKeyCount; foreignKeyIndex++)
            {
                var targetTable = tableNames[rand.Next(_tableCount)];
                var foreignKeyColumn = $"{targetTable}_id_{foreignKeyIndex}";

                columns = columns.Append($"`{foreignKeyColumn}` INT NULL");

                constraints.AppendLine(
                    $"ALTER TABLE `{tableName}` ADD CONSTRAINT `fk_{tableName}_{foreignKeyColumn}` FOREIGN KEY (`{foreignKeyColumn}`) REFERENCES `{targetTable}` (`id`);");
            }

            sql.AppendLine()
                .AppendLine($"CREATE TABLE {tableName} (")
                .Append("    ")
                .AppendLine($"{string.Join($",{Environment.NewLine}    ", columns)}")
                .AppendLine(");");
        }

        sql.AppendLine()
            .Append(constraints);

        return sql.ToString();
    }

    private string GetColumnDefinition(int columnIndex)
    {
        var columnType = _columnTypes[columnIndex % _columnTypes.Length];
        return $"`column{columnIndex}` {columnType} NULL";
    }
}
