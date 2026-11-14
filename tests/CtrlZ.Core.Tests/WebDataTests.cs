using CtrlZ.Core.Services;
using Microsoft.Data.Sqlite;

namespace CtrlZ.Core.Tests;

public class WebDataTests
{
    [Fact]
    public void Sanitizer_keeps_search_engines_and_removes_autofill_and_cards()
    {
        using var temp = new TestDirectory();
        var source = temp.PathOf("Web Data");
        using (var connection = new SqliteConnection($"Data Source={source};Mode=ReadWriteCreate;Pooling=False"))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE keywords (id INTEGER PRIMARY KEY, keyword TEXT, url TEXT);");
            Execute(connection, "INSERT INTO keywords (keyword, url) VALUES ('example', 'https://example.com/?q={searchTerms}');");
            Execute(connection, "CREATE TABLE autofill (name TEXT, value TEXT);");
            Execute(connection, "INSERT INTO autofill (name, value) VALUES ('Ada', 'ada@example.com');");
            Execute(connection, "CREATE TABLE credit_cards (name_on_card TEXT, number_encrypted BLOB);");
            Execute(connection, "INSERT INTO credit_cards (name_on_card) VALUES ('Ada Lovelace');");
        }

        var destination = temp.PathOf("Web Data Sanitized");
        WebDataSanitizer.Sanitize(source, destination);

        using var verify = new SqliteConnection($"Data Source={destination};Mode=ReadOnly;Pooling=False");
        verify.Open();
        Assert.Equal(1, Scalar(verify, "SELECT COUNT(*) FROM keywords;"));
        Assert.Equal(0, Scalar(verify, "SELECT COUNT(*) FROM autofill;"));
        Assert.Equal(0, Scalar(verify, "SELECT COUNT(*) FROM credit_cards;"));
        using var keywordCommand = verify.CreateCommand();
        keywordCommand.CommandText = "SELECT keyword FROM keywords LIMIT 1;";
        Assert.Equal("example", Convert.ToString(keywordCommand.ExecuteScalar()));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
