using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LocalDiskBrowser.Controllers
{
    public class AccountController : Controller
    {
        private readonly IConfiguration _config;
        public AccountController(IConfiguration config) => _config = config;

        [AllowAnonymous, HttpGet]
        public IActionResult Login()
        {
            if (User.Identity?.IsAuthenticated == true) return Redirect(Url.Content("~/"));
            return View();
        }

        [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string? username, string? password, bool rememberMe)
        {
            var expectedUser = _config["Auth:Username"] ?? "";
            var expectedPass = _config["Auth:Password"] ?? "";

            var ok = expectedPass.Length > 0
                     && Same(username ?? "", expectedUser)
                     & Same(password ?? "", expectedPass);
            if (!ok)
            {
                await Task.Delay(1000); // slow down guessing
                ViewData["Error"] = "Invalid username or password.";
                return View();
            }

            var id = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, expectedUser) }, CookieAuthenticationDefaults.AuthenticationScheme);
            // Remember me: persistent cookie that survives closing the browser (1 day).
            // Otherwise a session cookie (gone when the browser closes, 8 h limit from Program.cs).
            var props = new AuthenticationProperties();
            if (rememberMe)
            {
                props.IsPersistent = true;
                props.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1);
            }
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id), props);
            return Redirect(Url.Content("~/"));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        private static bool Same(string a, string b) =>
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
    }
}
