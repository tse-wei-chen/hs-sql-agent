using System.ComponentModel.DataAnnotations;

namespace Auth.Service.Models;

public class MfaSetupVM
{
    public string Secret { get; set; } = string.Empty;
    public string OtpAuthUri { get; set; } = string.Empty;
}

public class MfaCodeRequest { [Required] public required string Code { get; set; } }
public class ExternalCodeRequest { [Required] public required string Code { get; set; } }

public class MfaStatusVM
{
    public bool Enabled { get; set; }
    public int RecoveryCodesRemaining { get; set; }
}
