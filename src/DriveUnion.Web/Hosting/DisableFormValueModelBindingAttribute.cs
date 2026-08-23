using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DriveUnion.Web.Hosting;

/// <summary>
/// Strips the form value providers from an action so MVC cannot decide to read the request body.
///
/// The chunk endpoint hands <c>Request.Body</c> to the upload coordinator as a forward-only stream.
/// If anything reads it first the stream is consumed, and if a form provider reads it the whole
/// chunk is buffered — thirty-two megabytes per concurrent upload, on a route whose entire purpose
/// is not to hold bytes. A browser sending a Blob picks its own <c>Content-Type</c>, so relying on
/// "it will never look like a form" is not a guarantee worth taking.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
