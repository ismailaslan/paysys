using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Paysys.Api.OpenApi;

// Declares the JWT bearer scheme in the generated OpenAPI document, so a viewer
// (Swagger UI, Scalar) shows an Authorize box and sends "Authorization: Bearer <token>".
//
// The scheme is only declared if the app really registered "Bearer" authentication, so the
// document cannot advertise a scheme the API does not enforce.
//
// Known limitation: the requirement is added to every operation, including the login
// endpoint and the bank stub, which do not need a token. The viewer shows a lock on those
// too; it is cosmetic - the API itself still enforces authorization per endpoint.
internal sealed class BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider schemeProvider)
    : IOpenApiDocumentTransformer
{
    private const string SchemeName = "Bearer";

    public async Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemes = await schemeProvider.GetAllSchemesAsync();
        if (!schemes.Any(s => s.Name == SchemeName))
            return;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            [SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                In = ParameterLocation.Header,
                BearerFormat = "JWT",
            },
        };

        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations ?? []))
        {
            operation.Value.Security ??= [];
            operation.Value.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SchemeName, document)] = [],
            });
        }
    }
}
