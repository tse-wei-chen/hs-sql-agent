namespace Modules.Data.Entites;

public class SuperUser
{
    public int Id { get; set; }
    public string Username { get; set; } = null!;
    public string Mail { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
}