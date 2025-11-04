using Neo.Domain.Features.ObjectStore.Dto;

namespace Neo.Domain.Features.ObjectStore;

public interface IObjectStoreService
{
    Task<bool> HasAsync(string objectId);
    Task<string?> CheckSumAsync(string objectId);
    Task<string?> UploadFileAsync(string objectId, ObjectStoreDto fileData);
    Task<ObjectStoreDto?> DownloadFileAsync(string objectId);
}
