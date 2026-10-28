using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace XpressTest.NotificationService.Email;

public record EmailMessage(string To, string ToName, string Subject, string HtmlBody, string TextBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public enum EmailDelivery
{
    /// <summary>Writes each email as an .eml file (open it with any mail client). Development default.</summary>
    Pickup,
    Smtp
}

public sealed class EmailOptions
{
    public string From { get; set; } = "orders@xpress.local";
    public string FromName { get; set; } = "Xpress Orders";
    public EmailDelivery Delivery { get; set; } = EmailDelivery.Pickup;
    public string PickupDirectory { get; set; } = "mail-pickup";
    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
}

internal static class MimeBuilder
{
    public static MimeMessage Build(EmailOptions options, EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.From));
        mime.To.Add(new MailboxAddress(message.ToName, message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();
        return mime;
    }
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(o.Smtp.Host, o.Smtp.Port,
            o.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(o.Smtp.UserName))
            await smtp.AuthenticateAsync(o.Smtp.UserName, o.Smtp.Password ?? string.Empty, ct);
        await smtp.SendAsync(MimeBuilder.Build(o, message), ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}

public sealed class PickupDirectoryEmailSender(IOptions<EmailOptions> options, IHostEnvironment env, ILogger<PickupDirectoryEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        var directory = Path.GetFullPath(o.PickupDirectory, env.ContentRootPath);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml");

        await MimeBuilder.Build(o, message).WriteToAsync(path, ct);
        logger.LogInformation("Email \"{Subject}\" written to {Path}", message.Subject, path);
    }
}
