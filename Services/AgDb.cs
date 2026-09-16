using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace APISwitch.Services;

public static class AgDb
{
    public static Dictionary<string, string> ReadAuth(string dbPath)
    {
        var result = new Dictionary<string, string>();
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5,
        }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        var keys = string.Join(",", AgState.AuthKeys.Select((_, i) => $"$k{i}"));
        cmd.CommandText = $"SELECT key, value FROM ItemTable WHERE key IN ({keys})";
        for (int i = 0; i < AgState.AuthKeys.Length; i++)
            cmd.Parameters.AddWithValue($"$k{i}", AgState.AuthKeys[i]);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var val = reader.IsDBNull(1) ? null : reader.GetValue(1);
            var text = val switch
            {
                string s => s,
                byte[] b => Encoding.UTF8.GetString(b),
                _ => val?.ToString(),
            };
            if (text != null) result[key] = text;
        }
        return result;
    }

    public static void WriteAuth(string dbPath, Dictionary<string, string> values)
    {
        WriteOne(dbPath, values);
        var backup = dbPath + ".backup";
        if (File.Exists(backup)) WriteOne(backup, values);
    }

    public static void ClearAuth(string dbPath)
    {
        WriteAuth(dbPath, new Dictionary<string, string>());
    }

    static void WriteOne(string dbPath, Dictionary<string, string> values)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    DefaultTimeout = 5,
                }.ToString();
                using var conn = new SqliteConnection(connStr);
                conn.Open();
                using var tx = conn.BeginTransaction();
                foreach (var key in AgState.AuthKeys)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.Parameters.AddWithValue("$k", key);
                    if (values.TryGetValue(key, out var v))
                    {
                        cmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ($k, $v)";
                        cmd.Parameters.AddWithValue("$v", v);
                    }
                    else
                    {
                        cmd.CommandText = "DELETE FROM ItemTable WHERE key = $k";
                    }
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                return;
            }
            catch (SqliteException) when (attempt < 5)
            {
                Thread.Sleep(400);
            }
        }
    }
}
