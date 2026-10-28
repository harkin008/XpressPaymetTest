using System.ComponentModel.DataAnnotations;
using XpressTest.Web.Services;

namespace XpressTest.Web.Models;

public class LoginViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Keep me signed in")]
    public bool RememberMe { get; set; }
}

public class RegisterViewModel
{
    [Required, StringLength(100), Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 6), DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), Compare(nameof(Password)), Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class OrderFormViewModel
{
    [Required, StringLength(200), Display(Name = "Product")]
    public string ProductName { get; set; } = string.Empty;

    [Range(1, 1000)]
    public int Quantity { get; set; } = 1;

    [Range(typeof(decimal), "0.01", "1000000"), Display(Name = "Unit price")]
    public decimal UnitPrice { get; set; }
}

public class OrderListViewModel
{
    public required PagedDto<OrderDto> Orders { get; init; }
    public string? Status { get; init; }
    public bool ShowCustomer { get; init; }
}

public class OrderDetailsViewModel
{
    public required OrderDto Order { get; init; }
    public bool IsAdmin { get; init; }
    public bool CanEdit => IsAdmin || Order.Status == OrderStatuses.Pending;
    public bool CanCancel => Order.Status != OrderStatuses.Cancelled && (IsAdmin || Order.Status == OrderStatuses.Pending);
}

public class UserListViewModel
{
    public required UserPageDto Users { get; init; }
    public string? Search { get; init; }
    public string? Role { get; init; }
    public bool? IsActive { get; init; }
}

public class CreateUserViewModel
{
    [Required, StringLength(100), Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 6), DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = "User";

    public IReadOnlyList<string> AssignableRoles { get; set; } = [];
}

public class EditUserViewModel
{
    public long Id { get; set; }

    [Required, StringLength(100), Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = string.Empty;

    public IReadOnlyList<string> AssignableRoles { get; set; } = [];
}

public class DashboardViewModel
{
    public UserStatsDto? Users { get; init; }
    public OrderStatsDto? Orders { get; init; }
}
