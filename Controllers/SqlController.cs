using Microsoft.AspNetCore.Mvc;

namespace LocalDiskBrowser.Controllers
{
    // The SQL Server editor page (opened in its own tab from the toolbar). The API is SqlApiController.
    public class SqlController : Controller
    {
        public IActionResult Index() => View();

        // SQL profiler, opened in its own tab from the editor (the connection key arrives in the URL #hash, never in a request).
        public IActionResult Profiler() => View();
    }
}
