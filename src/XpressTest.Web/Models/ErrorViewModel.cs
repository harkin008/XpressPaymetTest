namespace XpressTest.Web.Models;

public class ErrorViewModel
{
    public string? RequestId { get; set; }

    public int StatusCode { get; set; }

    public string Title { get; set; } = "Something went wrong";

    public string Message { get; set; } = "An unexpected error occurred. Please try again.";

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
