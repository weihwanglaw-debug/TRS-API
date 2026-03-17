using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TRS_API.Filters;

/// <summary>
/// Global action filter that validates ModelState before every action runs.
/// [ApiController] handles missing [Required] fields automatically,
/// but this filter adds validation for [EmailAddress], [MinLength], [Range], etc.
/// Returns 400 ValidationProblem with field-level details on failure.
/// </summary>
public class ValidateModelFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            context.Result = new ValidationProblemDetails(context.ModelState)
            {
                Status = StatusCodes.Status400BadRequest
            }.ToActionResult();
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}

internal static class ValidationProblemDetailsExtensions
{
    internal static IActionResult ToActionResult(this ValidationProblemDetails details) =>
        new ObjectResult(details) { StatusCode = details.Status };
}