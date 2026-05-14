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

        RuleFor(x => x.DbManagementId)
            .NotEmpty().WithMessage("Database Management ID is required.");
    }
}
