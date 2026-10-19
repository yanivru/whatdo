using Microsoft.Extensions.Options;
using WhatDo.StatusService.Push;

namespace WhatDo.StatusService.Endpoints;

public static class PushEndpoints
{
    public sealed record SubscribeKeys(string? P256dh, string? Auth);
    public sealed record SubscribeRequest(string? Endpoint, SubscribeKeys? Keys);
    public sealed record UnsubscribeRequest(string? Endpoint);

    public static void MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/push/public-key", (IOptions<PushOptions> options) =>
            options.Value.IsConfigured
                ? Results.Text(options.Value.PublicKey)
                : Results.NotFound());

        app.MapPost("/api/push/subscribe", (SubscribeRequest request, IOptions<PushOptions> options, IPushSubscriptionStore store) =>
        {
            if (!options.Value.IsConfigured)
            {
                return Results.NotFound();
            }

            // https-only: the server will POST to this URL, so don't accept arbitrary schemes/hosts like http or file.
            if (!IsHttpsUrl(request.Endpoint) ||
                string.IsNullOrWhiteSpace(request.Keys?.P256dh) ||
                string.IsNullOrWhiteSpace(request.Keys?.Auth))
            {
                return Results.BadRequest();
            }

            return store.TryAdd(new PushSubscriptionInfo(request.Endpoint!, request.Keys!.P256dh!, request.Keys.Auth!))
                ? Results.Ok()
                : Results.StatusCode(StatusCodes.Status429TooManyRequests);
        });

        app.MapPost("/api/push/unsubscribe", (UnsubscribeRequest request, IOptions<PushOptions> options, IPushSubscriptionStore store) =>
        {
            if (!options.Value.IsConfigured)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Endpoint))
            {
                return Results.BadRequest();
            }

            store.Remove(request.Endpoint);
            return Results.Ok();
        });
    }

    private static bool IsHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
