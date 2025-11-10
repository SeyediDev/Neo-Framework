using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Neo.Common.Attributes;

namespace Neo.Domain.Entities.Integrations;

public class ExternalApi : BaseCoreConfigAuditableEntity<int>
{
    [DisplayName("نام")]
    [InDisplayString]
    [MaxLength(128)]
    public string Name { get; set; } = null!;

    [DisplayName("شرح")]
    [MaxLength(512)]
    public string? Description { get; set; }

    [DisplayName("دسته‌بندی")]
    [MaxLength(128)]
    public string? Category { get; set; }

    [DisplayName("نسخه")]
    [MaxLength(32)]
    public string? Version { get; set; }

    [DisplayName("آدرس پایه")]
    [MaxLength(512)]
    public string BaseUrl { get; set; } = null!;

    [DisplayName("مسیر نسبی")]
    [MaxLength(512)]
    public string RelativePath { get; set; } = null!;

    [DisplayName("روش HTTP")]
    public ExternalApiHttpMethod Method { get; set; } = ExternalApiHttpMethod.Get;

    [DisplayName("نیاز به احراز هویت")]
    public bool RequiresAuthentication { get; set; }

    [DisplayName("استفاده از OAuth2")]
    public bool UseOAuth2 { get; set; }

    [DisplayName("نوع گرنت OAuth2")]
    public ExternalApiOAuthGrantType OAuthGrantType { get; set; } = ExternalApiOAuthGrantType.None;

    [DisplayName("آدرس سرویس صدور توکن")]
    [MaxLength(512)]
    public string? TokenEndpoint { get; set; }

    [DisplayName("شناسه مشتری")]
    [MaxLength(256)]
    public string? ClientId { get; set; }

    [DisplayName("رمز مشتری")]
    [MaxLength(512)]
    public string? ClientSecret { get; set; }

    [DisplayName("دامنه مجوز")]
    [MaxLength(512)]
    public string? Scope { get; set; }

    [DisplayName("منبع")]
    [MaxLength(512)]
    public string? Resource { get; set; }

    [DisplayName("شنونده")]
    [MaxLength(512)]
    public string? Audience { get; set; }

    [DisplayName("کاربر (برای گرنت رمز)")]
    [MaxLength(256)]
    public string? Username { get; set; }

    [DisplayName("رمز عبور (برای گرنت رمز)")]
    [MaxLength(512)]
    public string? Password { get; set; }

    [DisplayName("روش احراز هویت مشتری")]
    [MaxLength(64)]
    public string? ClientAuthenticationMethod { get; set; }

    [DisplayName("پارامترهای اضافه احراز هویت")]
    [Column(TypeName = "nvarchar(max)")]
    public string? AdditionalAuthParametersJson { get; set; }

    [DisplayName("زمان نگهداری توکن (ثانیه)")]
    public int? TokenLifetimeSeconds { get; set; }

    [DisplayName("جبران اختلاف زمان (ثانیه)")]
    public int? TokenClockSkewSeconds { get; set; }

    [DisplayName("طرح احراز هویت")]
    [MaxLength(128)]
    public string? AuthenticationScheme { get; set; }

    [DisplayName("هدرهای پیش‌فرض")]
    [Column(TypeName = "nvarchar(max)")]
    public string? DefaultHeadersJson { get; set; }

    [DisplayName("مستندات OpenAPI")]
    [Column(TypeName = "nvarchar(max)")]
    public string? OpenApiDocumentJson { get; set; }

    [DisplayName("پارامترها")]
    public virtual ICollection<ExternalApiParameter> Parameters { get; set; } = [];

    [DisplayName("پاسخ‌ها")]
    public virtual ICollection<ExternalApiResponse> Responses { get; set; } = [];
}
