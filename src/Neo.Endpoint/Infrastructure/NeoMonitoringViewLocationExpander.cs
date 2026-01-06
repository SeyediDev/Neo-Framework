using Microsoft.AspNetCore.Mvc.Razor;

namespace Neo.Endpoint.Infrastructure;

/// <summary>
/// View Location Expander برای پیدا کردن Views در Neo.Endpoint
/// </summary>
public class NeoMonitoringViewLocationExpander : IViewLocationExpander
{
    public void PopulateValues(ViewLocationExpanderContext context)
    {
        // هیچ مقداری نیاز نیست
    }

    public IEnumerable<string> ExpandViewLocations(ViewLocationExpanderContext context, IEnumerable<string> viewLocations)
    {
        // فقط برای MonitoringController از Neo.Endpoint Views استفاده می‌کنیم
        if (context.ControllerName != "Monitoring")
        {
            // برای سایر Controller ها، فقط مسیرهای پیش‌فرض را برمی‌گردانیم
            foreach (var location in viewLocations)
            {
                yield return location;
            }
            yield break;
        }
        
        // اولویت اول: Views از output directory پروژه فعلی (که Views در آن کپی شده‌اند)
        // این برای زمانی است که Neo.Endpoint به عنوان reference استفاده می‌شود
        // استفاده از AppContext.BaseDirectory برای پیدا کردن base directory پروژه فعلی
        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            var currentOutputViewsPath = Path.Combine(baseDirectory, "Views");
            
            if (Directory.Exists(currentOutputViewsPath))
            {
                // مسیر Views در output directory پروژه فعلی
                yield return Path.Combine(currentOutputViewsPath, "{1}", "{0}.cshtml").Replace('\\', '/');
                yield return Path.Combine(currentOutputViewsPath, "{1}", "{0}.vbhtml").Replace('\\', '/');
                yield return Path.Combine(currentOutputViewsPath, "Shared", "{0}.cshtml").Replace('\\', '/');
                yield return Path.Combine(currentOutputViewsPath, "Shared", "{0}.vbhtml").Replace('\\', '/');
            }
        }
        
        // اولویت دوم: Views از Neo.Endpoint assembly location
        var neoEndpointAssembly = typeof(Controller.Api.MonitoringController).Assembly;
        var assemblyLocation = neoEndpointAssembly.Location;
        
        // بررسی مسیر output directory
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            var outputViewsPath = Path.Combine(Path.GetDirectoryName(assemblyLocation) ?? "", "Views");
            
            if (Directory.Exists(outputViewsPath))
            {
                // مسیر Views در output directory
                yield return Path.Combine(outputViewsPath, "{1}", "{0}.cshtml").Replace('\\', '/');
                yield return Path.Combine(outputViewsPath, "{1}", "{0}.vbhtml").Replace('\\', '/');
                yield return Path.Combine(outputViewsPath, "Shared", "{0}.cshtml").Replace('\\', '/');
                yield return Path.Combine(outputViewsPath, "Shared", "{0}.vbhtml").Replace('\\', '/');
            }
        }
        
        // بررسی مسیر source directory (برای Development)
        // پیدا کردن مسیر source از assembly location
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            // مسیر assembly: D:\Projects\Neo\src\Neo.Endpoint\bin\Debug\net8.0\Neo.Endpoint.dll
            // مسیر project: D:\Projects\Neo\src\Neo.Endpoint
            var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? ""; // bin\Debug\net8.0
            
            // استفاده از مسیر نسبی برای پیدا کردن Views (قابل اعتمادتر)
            var relativeViewsPath = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "Views"));
            
            // مسیرهای مختلف برای پیدا کردن Views
            var possiblePaths = new[]
            {
                relativeViewsPath, // مسیر نسبی از bin\Debug\net8.0: D:\Projects\Neo\src\Neo.Endpoint\Views
            };
            
            foreach (var sourceViewsPath in possiblePaths)
            {
                if (Directory.Exists(sourceViewsPath))
                {
                    yield return Path.Combine(sourceViewsPath, "{1}", "{0}.cshtml").Replace('\\', '/');
                    yield return Path.Combine(sourceViewsPath, "{1}", "{0}.vbhtml").Replace('\\', '/');
                    yield return Path.Combine(sourceViewsPath, "Shared", "{0}.cshtml").Replace('\\', '/');
                    yield return Path.Combine(sourceViewsPath, "Shared", "{0}.vbhtml").Replace('\\', '/');
                    break; // فقط اولین مسیر معتبر را اضافه می‌کنیم
                }
            }
        }

        // سپس مسیرهای پیش‌فرض
        foreach (var location in viewLocations)
        {
            yield return location;
        }
    }
}

