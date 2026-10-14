using BelgeRag.Api.Auth;
using BelgeRag.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BelgeRag.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(JwtTokenService tokens) : ControllerBase
{
    /// <summary>Kullanıcı adı/parola ile JWT erişim token'ı alır.</summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [ProducesResponseType<IssuedToken>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IssuedToken> Token(LoginRequest request)
    {
        var issued = tokens.Login(request.Username, request.Password);
        return issued is null ? Unauthorized() : Ok(issued);
    }
}
