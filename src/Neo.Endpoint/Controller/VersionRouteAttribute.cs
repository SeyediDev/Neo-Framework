using Microsoft.AspNetCore.Mvc;

namespace Neo.Endpoint.Controller;


public class VersionRouteAttribute(string template) : RouteAttribute($"api/v{{version:apiVersion}}/{template}")
{
}
public class AppRouteAttribute(string appName, string template) : VersionRouteAttribute($"{appName}/{template}")
{
}
