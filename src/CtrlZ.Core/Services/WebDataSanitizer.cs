using Microsoft.Data.Sqlite;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Services;

/// <summary>
/// Copies the Search Engines database while removing autofill/payment tables. Chrome keeps
/// search engines and several site settings in "Web Data"; copying it raw would leak more
/// personal data than the user asked to back up.
/// </summary>
public static class WebDataSanitizer
{
    private static readonly string[] SensitiveTables =
    [
        "autofill",
        "autofill_profiles",
        "autofill_profile_names",
        "autofill_profile_emails",
        "autofill_profile_phones",
        "autofill_profile_addresses",
        "credit_cards",
        "masked_credit_cards",
        "local_ibans",
        "server_addresses",
        "payments_customer_data",
        "contact_info",
        "contact_info_type",
        "contact_info_value",
        "token_service"
    ];

    public static void Sanitize(string sourcePath, string destinationPath, IAppLog? log = null)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Web Data was not found.", sourcePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        DeleteDatabaseFiles(destinationPath);

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var targetBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };

        try
        {
            using var source = new SqliteConnection(sourceBuilder.ToString());
            source.Open();
            using var target = new SqliteConnection(targetBuilder.ToString());
            target.Open();
            source.BackupDatabase(target);
            target.Close();
        }
        catch (Exception ex)
        {
            log?.Write(LogLevel.Warning, "Could not copy Web Data for sanitization.", ex);
            DeleteDatabaseFiles(destinationPath);
            throw;
        }

        try
        {
            using var connection = new SqliteConnection(targetBuilder.ToString());
            connection.Open();
            foreach (var table in SensitiveTables)
            {
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"DELETE FROM \"{table}\";";
                    command.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // Table absent in this browser version.
                }
            }

            using (var vacuum = connection.CreateCommand())
            {
                vacuum.CommandText = "VACUUM;";
                vacuum.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            log?.Write(LogLevel.Warning, "Could not remove sensitive rows from Web Data.", ex);
            DeleteDatabaseFiles(destinationPath);
            throw;
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try { if (File.Exists(candidate)) File.Delete(candidate); } catch { }
        }
    }
}
