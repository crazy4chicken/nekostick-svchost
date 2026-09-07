using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Compose;
using Nekolla.Nekostick.ServiceHost.Settings;
using Nekolla.Nekostick.ServiceHost.Sync;

namespace Nekolla.Nekostick.ServiceHost.Api;

public sealed partial class SvchostApiHandler
{
    private async ValueTask<ExtensionStreamingResponse> HandleStatusAsync(CancellationToken cancellationToken)
    {
        var read = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return ErrorForConfiguration(read.Errors);
        }
        return JsonResponse(
            200,
            new
            {
                bootstrap = _apiKeyService.IsBootstrap,
                version = ExtensionVersion,
                dataDirectoryAvailable = !string.IsNullOrWhiteSpace(_bridge.DataDirectory),
                configs = read.Value?.Settings?.Configs.Count ?? 0
            });
    }

    private async ValueTask<ExtensionStreamingResponse> HandleBootstrapKeyAsync(
        Stream bodyStream,
        CancellationToken cancellationToken)
    {
        if (!_apiKeyService.IsBootstrap)
        {
            return Error(403, "forbidden", "The extension is not waiting for a bootstrap key.");
        }

        var body = await ReadBodyAsync(bodyStream, cancellationToken).ConfigureAwait(false);
        if (body.TooLarge)
        {
            return Error(413, "request_too_large", "The request body exceeds the 1 MiB limit.");
        }

        BootstrapKeyRequest? input;
        try
        {
            input = JsonSerializer.Deserialize<BootstrapKeyRequest>(body.Bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return Error(422, "validation", "The request body must be valid JSON.");
        }

        if (input is null || string.IsNullOrWhiteSpace(input.ApiKey) ||
            input.ApiKey.Length < ApiKeyService.MinimumKeyLength)
        {
            return Error(422, "validation", $"apiKey must contain at least {ApiKeyService.MinimumKeyLength} characters.");
        }

        var write = await _apiKeyService.SetPermanentKeyAsync(input.ApiKey, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess)
        {
            return ErrorForConfiguration(write.Errors);
        }

        return await HandleStatusAsync(cancellationToken).ConfigureAwait(false);
    }
}
