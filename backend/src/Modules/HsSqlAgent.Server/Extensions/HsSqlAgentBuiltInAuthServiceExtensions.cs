using System.Text;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Auth.Service.Services;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Background;
using HsSqlAgent.Server.Filters;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentBuiltInAuthServiceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentBuiltInAuth(this HsSqlAgentRegistrationBuilder builder)
    {
        if (builder.IsRegistered("host-authorization"))
        {
            throw new InvalidOperationException(
                "HsSqlAgent built-in authentication and host authorization are mutually exclusive authorization modes.");
        }

        builder.AddHsSqlAgentAdminStore();
        if (!builder.TryRegister("built-in-auth")) return builder;

        var services = builder.Services;
        var options = builder.Options;
        if (string.IsNullOrWhiteSpace(options.JwtSecretKey) || Encoding.UTF8.GetByteCount(options.JwtSecretKey) < 32)
            throw new InvalidOperationException("JwtSecretKey must be at least 32 bytes.");

        services.AddAuthDatabase(options.AdminDatabaseProvider, options.AdminConnectionString);
        services.AddDataProtection();
        IDataProtectionProvider? isolatedDataProtectionProvider = null;
        if (!string.IsNullOrWhiteSpace(options.EnterpriseIdentity.DataProtectionKeyPath))
        {
            var keyPath = Path.GetFullPath(options.EnterpriseIdentity.DataProtectionKeyPath, AppContext.BaseDirectory);
            Directory.CreateDirectory(keyPath);
            isolatedDataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(keyPath),
                dataProtection => dataProtection.SetApplicationName("HsSqlAgent"));
        }

        services.AddScoped<IMemberService, MemberService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IEnterpriseIdentityService, EnterpriseIdentityService>();
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<ITokenRevocationService, TokenRevocationService>();
        services.AddSingleton<IAuthRuntimeStateCache, AuthRuntimeStateCache>();

        services.Configure<JwtSettings>(jwt =>
        {
            jwt.SecretKey = options.JwtSecretKey;
            jwt.Issuer = options.JwtIssuer;
            jwt.Audience = options.JwtAudience;
            jwt.AccessTokenExpirationMinutes = options.JwtAccessTokenExpirationMinutes;
            jwt.RefreshTokenExpirationDays = options.JwtRefreshTokenExpirationDays;
            jwt.SignInLockoutThreshold = options.SignInLockoutThreshold;
            jwt.SignInLockoutMinutes = options.SignInLockoutMinutes;
        });
        services.Configure<PasswordResetSettings>(reset =>
        {
            reset.BaseUrl = options.PasswordResetBaseUrl;
            reset.ExpirationMinutes = options.PasswordResetExpirationMinutes;
            reset.SmtpHost = options.SmtpHost;
            reset.SmtpPort = options.SmtpPort;
            reset.SmtpEnableSsl = options.SmtpEnableSsl;
            reset.SmtpUsername = options.SmtpUsername;
            reset.SmtpPassword = options.SmtpPassword;
            reset.SmtpFrom = options.SmtpFrom;
        });
        services.Configure<EnterpriseIdentitySettings>(identity =>
        {
            var source = options.EnterpriseIdentity;
            identity.OidcEnabled = source.OidcEnabled;
            identity.Authority = source.Authority;
            identity.ClientId = source.ClientId;
            identity.ClientSecret = source.ClientSecret;
            identity.RequireHttpsMetadata = source.RequireHttpsMetadata;
            identity.EmailClaim = source.EmailClaim;
            identity.NameClaim = source.NameClaim;
            identity.RoleClaim = source.RoleClaim;
            identity.EmailVerifiedClaim = source.EmailVerifiedClaim;
            identity.RequireVerifiedEmail = source.RequireVerifiedEmail;
            identity.Scopes = [.. source.Scopes];
            identity.RoleMappings = new(source.RoleMappings, StringComparer.OrdinalIgnoreCase);
            identity.DefaultRoleNames = [.. source.DefaultRoleNames];
            identity.AutoProvision = source.AutoProvision;
            identity.FrontendCallbackUrl = source.FrontendCallbackUrl;
            identity.LoginCodeExpirationMinutes = source.LoginCodeExpirationMinutes;
            identity.RequireMfaForRoles = [.. source.RequireMfaForRoles];
            identity.TotpIssuer = source.TotpIssuer;
        });

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSecretKey));
        var authentication = services.AddAuthentication()
            .AddJwtBearer(HsSqlAgentAuthenticationSchemes.Bearer, jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidIssuer = options.JwtIssuer,
                    ValidAudience = options.JwtAudience,
                    IssuerSigningKey = signingKey,
                    ClockSkew = TimeSpan.Zero
                };
            })
            .AddCookie(HsSqlAgentAuthenticationSchemes.ExternalCookie, cookie =>
            {
                cookie.Cookie.Name = "hs-sql-agent.external";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                if (isolatedDataProtectionProvider is not null)
                    cookie.DataProtectionProvider = isolatedDataProtectionProvider;
            });

        if (options.EnterpriseIdentity.OidcEnabled)
        {
            if (string.IsNullOrWhiteSpace(options.EnterpriseIdentity.Authority) ||
                string.IsNullOrWhiteSpace(options.EnterpriseIdentity.ClientId))
                throw new InvalidOperationException("OIDC Authority and ClientId are required when OIDC is enabled.");
            authentication.AddOpenIdConnect(HsSqlAgentAuthenticationSchemes.Oidc, oidc =>
            {
                var source = options.EnterpriseIdentity;
                oidc.SignInScheme = HsSqlAgentAuthenticationSchemes.ExternalCookie;
                oidc.Authority = source.Authority;
                oidc.ClientId = source.ClientId;
                oidc.ClientSecret = source.ClientSecret;
                oidc.RequireHttpsMetadata = source.RequireHttpsMetadata;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.SaveTokens = false;
                oidc.CallbackPath = "/api/auth/oidc/signin";
                oidc.Scope.Clear();
                foreach (var scope in source.Scopes) oidc.Scope.Add(scope);
                if (isolatedDataProtectionProvider is not null)
                {
                    oidc.StateDataFormat = new PropertiesDataFormat(
                        isolatedDataProtectionProvider.CreateProtector("HsSqlAgent.Server", "OidcState", "v1"));
                }
            });
        }

        // HsSqlAgent authorization filters invoke the namespaced schemes directly. Core authorization
        // services remain available for host-policy mode, but built-in identity never owns host defaults.
        services.AddAuthorization();

        services.RemoveAll<IHsSqlAgentPermissionAuthorizer>();
        services.AddScoped<PermissionAuthorizationHandler>();
        services.AddScoped<IHsSqlAgentPermissionAuthorizer>(sp => sp.GetRequiredService<PermissionAuthorizationHandler>());
        services.AddScoped<HsSqlAgentBuiltInAuthStateFilter>();
        services.AddHostedService<TokenBlacklistCleanupService>();

        return builder;
    }
}
