using System.ComponentModel.DataAnnotations;
using XpressTest.AuthService.Data.Queries;

namespace XpressTest.AuthService.ViewModels;

public class UserListItemViewModel
{
    public required long Id { get; init; }
    public required string FullName { get; init; }
    public required string Email { get; init; }
    public required string Role { get; init; }
    public bool IsActive { get; init; }
    public bool CanManage { get; init; }
    public bool CanDelete { get; init; }
}

public class UserListViewModel
{
    public required IReadOnlyList<UserListItemViewModel> Items { get; init; }
    public required UserFilter Filter { get; init; }
    public int Page { get; init; }
    public int TotalPages { get; init; }
    public int TotalCount { get; init; }
}

public class CreateUserViewModel
{
    [Required, StringLength(100)]
    [Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 6), DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = string.Empty;

    public IReadOnlyList<string> AssignableRoles { get; set; } = [];
}

public class EditUserViewModel
{
    public long Id { get; set; }

    [Required, StringLength(100)]
    [Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = string.Empty;

    public IReadOnlyList<string> AssignableRoles { get; set; } = [];
}
