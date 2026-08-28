using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Planner.Infrastructure;

/// <summary>Maps the CLR PascalCase model onto snake_case Postgres identifiers. Done as a model-wide
/// pass rather than per-property attributes so it also covers Identity and OpenIddict's own tables,
/// which we do not own the source of.</summary>
public static class NamingConventions
{
    public static void ApplySnakeCase(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is not null)
            {
                entity.SetTableName(ToSnakeCase(table));
            }

            foreach (var property in entity.GetProperties())
            {
                // Preserve explicitly configured column names such as the xmin system column.
                var current = property.GetColumnName();
                property.SetColumnName(ToSnakeCase(current ?? property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                if (key.GetName() is { } name)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                if (fk.GetConstraintName() is { } name)
                {
                    fk.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                if (index.GetDatabaseName() is { } name)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c))
            {
                var previous = i > 0 ? value[i - 1] : '\0';
                var next = i + 1 < value.Length ? value[i + 1] : '\0';
                var boundary = i > 0 && previous != '_' &&
                               (!char.IsUpper(previous) || (char.IsUpper(previous) && char.IsLower(next)));
                if (boundary)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
