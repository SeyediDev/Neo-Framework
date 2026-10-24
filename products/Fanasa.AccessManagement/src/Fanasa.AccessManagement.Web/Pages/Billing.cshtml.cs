using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace Fanasa.AccessManagement.Web.Pages;
public sealed class BillingModel : PageModel { public IActionResult OnGet(Guid? tenant) => RedirectToPage("/Accounting", new { tenant }); }
