namespace Auth.Service.Models;

public class MemberQuery
{
    public string? Search { get; set; }
    public bool? IsActive { get; set; }
    public int? RoleId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class MemberPage
{
    public IReadOnlyCollection<MemberVM> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
