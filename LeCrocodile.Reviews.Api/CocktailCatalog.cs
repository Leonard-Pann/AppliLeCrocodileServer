using System.Collections.Frozen;
using System.Text.Json;

namespace LeCrocodile.Reviews.Api;

public sealed class CocktailCatalog
{
    private readonly FrozenSet<string> ids;

    public CocktailCatalog(string path)
    {
        using FileStream stream = File.OpenRead(path);
        string[] entries = JsonSerializer.Deserialize<string[]>(stream)
            ?? throw new InvalidDataException("The cocktail catalog cannot be null.");
        if (entries.Length == 0 || entries.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)
            || entries.Distinct(StringComparer.Ordinal).Count() != entries.Length)
        {
            throw new InvalidDataException("The cocktail catalog must contain unique, nonempty IDs of at most 128 characters.");
        }

        ids = entries.ToFrozenSet(StringComparer.Ordinal);
    }

    public bool Contains(string cocktailId) => ids.Contains(cocktailId);
}
