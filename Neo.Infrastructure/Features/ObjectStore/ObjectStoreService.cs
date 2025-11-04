using System.Text;
using System.Text.Json;
using Neo.Domain.Features.ObjectStore;
using Neo.Domain.Features.ObjectStore.Dto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace Neo.Infrastructure.Features.ObjectStore;

public class ObjectStoreService(IMinioClient minioClient, ILogger<ObjectStoreService> logger, IConfiguration configuration)
    : IObjectStoreService
{
    public async Task<bool> HasAsync(string objectId)
    {
        return !string.IsNullOrEmpty(await CheckSumAsync(objectId));
    }

    public async Task<string?> CheckSumAsync(string objectId)
    {
        try
        {
            var s = await minioClient.StatObjectAsync(new StatObjectArgs()
              .WithBucket(configuration["Minio:Bucket"])
              .WithObject(objectId));
            return s.ETag;
        }
        catch (ObjectNotFoundException)
        {
            logger.LogWarning("Minio object not found {objectId}", objectId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError("Minio expection {@expection}", ex);
            throw;
        }
    }
    public async Task<string?> UploadFileAsync(string objectId, ObjectStoreDto fileData)
    {
        try
        {
            var json = JsonSerializer.Serialize(fileData);
            var jsonArray = Encoding.ASCII.GetBytes(json);
            using var stream = new MemoryStream(jsonArray);
            var response = await minioClient.PutObjectAsync(new PutObjectArgs()
                 .WithBucket(configuration["Minio:Bucket"])
                 .WithObject(objectId)
                 .WithStreamData(stream)
                 .WithObjectSize(jsonArray.Length)
             );
            return response.Etag;
        }
        catch (Exception ex)
        {
            logger.LogError("Minio expection {@expection}", ex);
            throw;
        }
    }

    public async Task<ObjectStoreDto?> DownloadFileAsync(string objectId)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            await minioClient.GetObjectAsync(new GetObjectArgs()
                 .WithBucket(configuration["Minio:Bucket"])
                 .WithObject(objectId)
                 .WithCallbackStream(stream => stream.CopyTo(memoryStream)));
            var fileArray = memoryStream.ToArray();
            var json = Encoding.ASCII.GetString(fileArray);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ObjectStoreDto>(json);
        }
        catch (ObjectNotFoundException)
        {
            logger.LogWarning("Minio object not found {objectId}", objectId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError("Minio error {@error}", ex);
            throw;
        }
    }
}
