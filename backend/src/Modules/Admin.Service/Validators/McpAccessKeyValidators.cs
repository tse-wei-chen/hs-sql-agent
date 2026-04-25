using FluentValidation;
using Admin.Service.Models;

namespace Admin.Service.Validators;

public class IssueMcpAccessKeyRequestValidator : AbstractValidator<IssueMcpAccessKeyRequest>
{
    public IssueMcpAccessKeyRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters.");

        RuleFor(x => x.ExpiresAt)
            .GreaterThan(DateTime.UtcNow).WithMessage("Expiration date must be in the future.")
            .When(x => x.ExpiresAt.HasValue);

        RuleFor(x => x.DbSettingMode)
            .InclusiveBetween(0, 1).WithMessage("Invalid Database Setting Mode.");
            
        RuleFor(x => x.Host)
            .NotEmpty().WithMessage("Host is required when using direct connection.")
            .When(x => x.DbSettingMode == 1);
            
        RuleFor(x => x.Database)
            .NotEmpty().WithMessage("Database name is required when using direct connection.")
            .When(x => x.DbSettingMode == 1);
    }
}
