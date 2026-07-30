namespace Admin.Service.Models;

public static class DbManagementPasswordPolicy
{
    public static bool RequiresPassword(string? sqlProvider)
        => !string.Equals(sqlProvider, "Sqlite", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(sqlProvider, "Global", StringComparison.OrdinalIgnoreCase);
}
