using System.Text;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Auth.Service.Services;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Background;
using HsSqlAgent.Server.Filters;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentBuiltInAuthServiceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentBuiltInAuth(
        this HsSqlAgentRegistrationBuilder builder,
        Action<HsSqlAgentBuiltInAuthOptions>? configure = null)
    {
        if (builder.IsRegistered("host-authorization"))
        {
            throw new InvalidOperationException(
                "HsSqlAgent built-in authentication and host authorization are mutually exclusive authorization modes.");
        }

        builder.AddHsSqlAgentAdminStore();
        builder.ThrowIfAlreadyConfigured("built-in-auth", configure);
        if (builder.IsRegistered("built-in-auth")) return builder;

        var options = builder.GetOrCreateOptions(() => builder.LegacyOptions is { } legacy
            ? HsSqlAgentBuiltInAuthOptions.FromLegacy(legacy)
            : new HsSqlAgentBuiltInAuthOptions());
        configure?.Invoke(options);
        if (!builder.TryRegister("built-in-auth")) return builder;

        var adminStore = builder.GetRequiredOptions<HsSqlAgentAdminStoreOptions>();
        var services = builder.Services;
        if (string.IsNullOrWhiteSpace(options.Jwt.SecretKey) || Encoding.UTF8.GetByteCount(options.Jwt.SecretKey) < 32)
            throw new InvalidOperationException("BuiltInAuth Jwt SecretKey must be at least 32 bytes.");

        services.AddAuthDatabase(adminStore.Provider, adminStore.ConnectionString);
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
            jwt.SecretKey = options.Jwt.SecretKey;
            jwt.Issuer = options.Jwt.Issuer;
            jwt.Audience = options.Jwt.Audience;
            jwt.AccessTokenExpirationMinutes = options.Jwt.AccessTokenExpirationMinutes;
            jwt.RefreshTokenExpirationDays = options.Jwt.RefreshTokenExpirationDays;
            jwt.SignInLockoutThreshold = options.Jwt.SignInLockoutThreshold;
            jwt.SignInLockoutMinutes = options.Jwt.SignInLockoutMinutes;
        });
        services.Configure<PasswordResetSettings>(reset =>
        {
            reset.BaseUrl = options.PasswordReset.BaseUrl;
            reset.ExpirationMinutes = options.PasswordReset.ExpirationMinutes;
            reset.SmtpHost = options.PasswordReset.SmtpHost;
            reset.SmtpPort = options.PasswordReset.SmtpPort;
            reset.SmtpEnableSsl = options.PasswordReset.SmtpEnableSsl;
            reset.SmtpUsername = options.PasswordReset.SmtpUsername;
            reset.SmtpPassword = options.PasswordReset.SmtpPassword;
            reset.SmtpFrom = options.PasswordReset.SmtpFrom;
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

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Jwt.SecretKey));
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
                    ValidIssuer = options.Jwt.Issuer,
                    ValidAudience = options.Jwt.Audience,
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

        services.AddAuthorization();

        services.RemoveAll<IHsSqlAgentPermissionAuthorizer>();
        services.AddScoped<PermissionAuthorizationHandler>();
        services.AddScoped<IHsSqlAgentPermissionAuthorizer>(sp => sp.GetRequiredService<PermissionAuthorizationHandler>());
        services.AddScoped<HsSqlAgentBuiltInAuthStateFilter>();
        services.AddHostedService<TokenBlacklistCleanupService>();

        return builder;
    }
}
