using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Organization;

/// <summary>HTTP mutation protection only for the explicitly marked educational tenant.</summary>
public static class TemplateOrganizationWriteGuard
{
    public const string Message = "سازمان الگو مرجع آموزشی فقط‌خواندنی است؛ تغییرات را در سازمان خود ثبت کنید.";

    public static ConflictObjectResult? Reject(TemplateOrganizationStore? examples, Guid tenant) =>
        examples?.IsTemplateTenant(tenant) == true
            ? new(new { code = "template-organization-read-only", error = Message })
            : null;
}
