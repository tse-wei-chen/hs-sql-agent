namespace Admin.Service.Data.Entites;

public class DbManagement
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? SqlProvider { get; set; }
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Username { get; set; }
    public string? PasswordHash { get; set; }
    public string? Database { get; set; }
    public string? ExtraSettings { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
