using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IBTM.Storage;

public sealed partial class MachineStore
{
    public long NextPcbNumber()
    {
        using var db = new MachineDb(_options);
        db.Database.OpenConnection();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "UPDATE PcbCounter SET Number = Number + 1 WHERE Id = 1 RETURNING Number";
        return (long)(command.ExecuteScalar()
            ?? throw new InvalidDataException("The PCB counter is missing."));
    }

    public void SavePcb(string databaseFile, PcbRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databaseFile }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS Pcbs (Number INTEGER NOT NULL PRIMARY KEY, Value TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO Pcbs (Number, Value) VALUES ($number, $value)
            ON CONFLICT(Number) DO UPDATE SET Value = excluded.Value
            """;
        command.Parameters.AddWithValue("$number", record.Number);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(record));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PcbRecord> LoadPcbs(string directory, long? beforeNumber = null, int count = 100)
    {
        var records = new List<PcbRecord>();
        if (!Directory.Exists(directory))
            return records;
        foreach (var file in Directory.EnumerateFiles(directory, "PCB-????-??.db")
            .OrderByDescending(Path.GetFileName))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Number, Value FROM Pcbs WHERE ($before IS NULL OR Number < $before) ORDER BY Number DESC LIMIT $count";
            command.Parameters.AddWithValue("$before", (object?)beforeNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("$count", count - records.Count);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = JsonSerializer.Deserialize<PcbRecord>(reader.GetString(1))
                    ?? throw new InvalidDataException($"PCB {reader.GetInt64(0)} has no result data.");
                records.Add(record with { Number = reader.GetInt64(0) });
            }
            if (records.Count == count)
                break;
        }
        return records;
    }
}
