namespace Hackathon.Assessment.Api.Middleware;

public static class ApiProblemResults
{
    public static IResult ValidationProblem(
        HttpContext context,
        IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: "The request is invalid.",
            instance: context.Request.Path,
            extensions: CorrelationExtension(context));

    public static IResult Problem(HttpContext context, int statusCode, string title) =>
        TypedResults.Problem(
            statusCode: statusCode,
            title: title,
            type: "about:blank",
            instance: context.Request.Path,
            extensions: CorrelationExtension(context));

    private static IDictionary<string, object?> CorrelationExtension(HttpContext context) =>
        new Dictionary<string, object?>
        {
            ["correlationId"] = CorrelationIdMiddleware.GetCorrelationId(context)
        };
}
