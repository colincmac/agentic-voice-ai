using Microsoft.AspNetCore.Http;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Endpoints;
public interface IDocumentEndpointHandler<TResult> where TResult : class
{
    /// <summary>
    /// Handles the endpoint request by setting the HttpResponse and returns a result. 
    /// </summary>
    /// <param name="context">The Http context for the endpoint request.</param>
    /// <returns>A task representing the asynchronous operation, with a result of type TResult.</returns>
    /// <remarks>
    /// By default, this is registered as a GET endpoint and expects the `HandleAsync` method to write the response directly to the HttpContext.Response.
    /// </remarks>
    Task<TResult?> HandleAsync(HttpContext context);
}
