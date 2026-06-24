using System.ComponentModel.DataAnnotations;

namespace Fcl.Sync.Service.Persistence;

public sealed class SqliteOptions
{
    public const string SectionName = "Sqlite";

    [Required]
    public string ConnectionString { get; init; } = "Data Source=data/fcl-sync.db";
}
