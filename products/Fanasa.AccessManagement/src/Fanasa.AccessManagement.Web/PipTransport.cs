using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Fanasa.AccessManagement.Web.Governance;

/// <summary>Bounded HTTPS ingestion of a correlated, explicitly mapped external attribute envelope.</summary>
public sealed class HttpPipExternalConnector(HttpClient http, TimeProvider clock, PipTransportOptions options, IPipSecretResolver? secrets = null,
    IPipHttpClientProvider? clients = null) : IPipExternalConnector
{
    private static readonly HashSet<string> EnvelopeFields = new(StringComparer.Ordinal)
        { "schemaVersion", "tenantId", "subject", "sourceVersion", "observedAt", "attributes" };

    public async Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
    {
        var binding = options.Authorize(source);
        AccessPolicyStore.Label(subject, 200);
        if (!source.Enabled) throw new InvalidOperationException("PIP source is disabled.");
        if (source.TimeoutSeconds is < 1 or > 120 || source.MaxResponseBytes is < 1024 or > 10 * 1024 * 1024
            || source.MaxAgeSeconds is < 1 or > 86400 || source.SchemaVersion is not (1 or 2))
            throw new InvalidOperationException("Invalid PIP transport limits.");
        var builder = new UriBuilder(PipTransportOptions.CanonicalEndpoint(source.Endpoint))
        {
            Query = $"tenantId={Uri.EscapeDataString(source.TenantId.ToString("D"))}&subject={Uri.EscapeDataString(subject)}"
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Options.Set(PipHttpTransport.BindingKey, binding);
        request.Headers.ConnectionClose = true;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds));
        if (source.SecretReference is not null)
        {
            if (secrets is null) throw new InvalidOperationException("Configured PIP credential could not be resolved.");
            string? bearer;
            try { bearer = await secrets.ResolveAsync(source.SecretReference, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TaskCanceledException("PIP synchronization exceeded the configured timeout."); }
            catch (OperationCanceledException) { throw; }
            catch { throw new InvalidOperationException("Configured PIP credential could not be resolved."); }
            if (string.IsNullOrWhiteSpace(bearer) || bearer.Length > 8192 || bearer.Any(char.IsWhiteSpace) || bearer.Any(char.IsControl))
                throw new InvalidOperationException("Configured PIP credential could not be resolved.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        if (source.LastEtag is not null)
        {
            if (source.LastEtag.Length > 512 || !EntityTagHeaderValue.TryParse(source.LastEtag, out var validator) || validator.Tag == "*")
                throw new InvalidDataException("Invalid PIP cache validator.");
            request.Headers.IfNoneMatch.Add(validator);
        }
        try
        {
            if (binding.CaFile is not null && clients is null) throw new InvalidOperationException("PIP custom trust requires the deployment client provider.");
            using var response = await (clients?.GetClient(binding) ?? http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (source.LastEtag is null) throw new InvalidDataException("Unsolicited PIP not-modified response.");
                if (response.Headers.ETag is { } responseValidator && responseValidator.Tag != request.Headers.IfNoneMatch.Single().Tag)
                    throw new InvalidDataException("PIP cache validator does not match the requested snapshot.");
                throw new PipNotModifiedException();
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"External PIP returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentType?.MediaType is not "application/json"
                || response.Content.Headers.ContentEncoding.Count > 0)
                throw new InvalidDataException("External PIP must return uncompressed application/json.");
            if (response.Content.Headers.ContentLength > source.MaxResponseBytes)
                throw new InvalidDataException("External PIP response exceeds the configured size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > source.MaxResponseBytes) throw new InvalidDataException("External PIP response exceeds the configured size limit.");
                body.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 4 });
            return Parse(document.RootElement, source, subject, response.Headers.ETag?.ToString());
        }
        catch (JsonException) { throw new InvalidDataException("External PIP returned malformed JSON."); }
        catch (HttpRequestException) { throw new HttpRequestException("PIP transport failed; verify approved endpoint, network, TLS and source availability."); }
        catch (IOException) { throw new InvalidDataException("External PIP response could not be read completely."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TaskCanceledException("PIP synchronization exceeded the configured timeout."); }
    }

    private PipFetchedSnapshot Parse(JsonElement root, PipSourceDefinition source, string subject, string? etag)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("External PIP envelope must be an object.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!(EnvelopeFields.Contains(field.Name) || source.SchemaVersion == 2 && field.Name == "validUntil") || !fields.Add(field.Name)) throw new InvalidDataException("External PIP envelope has duplicate or undeclared fields.");
        if (fields.Count != EnvelopeFields.Count + (source.SchemaVersion == 2 ? 1 : 0)) throw new InvalidDataException("External PIP envelope is incomplete.");
        var schema = root.GetProperty("schemaVersion");
        if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != source.SchemaVersion)
            throw new InvalidDataException("External PIP schema version does not match the source contract.");
        var tenantText = ReadString(root.GetProperty("tenantId"), 36);
        var returnedSubject = ReadString(root.GetProperty("subject"), 200);
        if (!Guid.TryParseExact(tenantText, "D", out var tenantId) || tenantId != source.TenantId || returnedSubject != subject)
            throw new InvalidDataException("External PIP response correlation failed.");
        var version = ReadString(root.GetProperty("sourceVersion"), 200);
        var observedText = ReadString(root.GetProperty("observedAt"), 40);
        if (!PipSchema.TryParseUtcDateTime(observedText, out var observedAt))
            throw new InvalidDataException("External PIP observation must be an ISO 8601 timestamp with explicit UTC timezone.");
        DateTimeOffset? validUntil = source.SchemaVersion == 2 ? PipSchema.ParseUtcDateTime(ReadString(root.GetProperty("validUntil"), 40)) : null;
        if (etag is { Length: > 512 }) throw new InvalidDataException("External PIP cache validator is too long.");
        var raw = root.GetProperty("attributes");
        if (raw.ValueKind != JsonValueKind.Object) throw new InvalidDataException("External PIP attributes must be an object.");
        var definitions = PipSchema.Definitions(source);
        PipSchema.Validate(definitions);
        var mapping = definitions.ToDictionary(x => x.ExternalName ?? x.Name, StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in raw.EnumerateObject())
        {
            if (!mapping.TryGetValue(field.Name, out var definition) || !values.TryAdd(definition.Name, ReadString(field.Value, 2000)))
                throw new InvalidDataException("External PIP attributes contain duplicate or undeclared fields.");
        }
        if (definitions.Any(x => x.Required && !values.ContainsKey(x.Name))) throw new InvalidDataException("External PIP response is missing a required attribute.");
        PipSchema.ValidateAttributes(source, values);
        var fetched = new PipFetchedSnapshot(tenantId, subject, schemaVersion, values, version, etag, observedAt,
            PipStore.HashAttributes(tenantId, subject, schemaVersion, values, version, observedAt, validUntil), validUntil);
        PipStore.ValidateFetchedSnapshot(source, subject, fetched, clock.GetUtcNow());
        return fetched;
    }

    private static string ReadString(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("External PIP fields must use the declared JSON types.");
        var value = element.GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("External PIP contains an invalid string value.");
        return value;
    }
}

public sealed class PipNotModifiedException : Exception;
