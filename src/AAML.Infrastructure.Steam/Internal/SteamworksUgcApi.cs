using Steamworks;

namespace AAML.Infrastructure.Steam.Internal;

internal sealed class SteamworksUgcApi : ISteamUgcApi
{
    private const uint PreviewUrlBufferSize = 2048;
    public SteamQueryHandle CreateDetailsQuery(IReadOnlyList<ulong> ids)
    {
        var nativeIds = ids.Select(id => new PublishedFileId_t(id)).ToArray();
        return new SteamQueryHandle(SteamUGC.CreateQueryUGCDetailsRequest(nativeIds, (uint)nativeIds.Length).m_UGCQueryHandle);
    }

    public bool SetReturnChildren(SteamQueryHandle handle, bool enabled) =>
        SteamUGC.SetReturnChildren(new UGCQueryHandle_t(handle.Value), enabled);

    public bool SetReturnLongDescription(SteamQueryHandle handle, bool enabled) => SteamUGC.SetReturnLongDescription(new UGCQueryHandle_t(handle.Value), enabled);

    public SteamAsyncCall SendQuery(SteamQueryHandle handle) =>
        new(SteamUGC.SendQueryUGCRequest(new UGCQueryHandle_t(handle.Value)).m_SteamAPICall);

    public bool TryGetQueryItem(SteamQueryHandle handle, uint index, out SteamWorkshopSnapshot item)
    {
        var nativeHandle = new UGCQueryHandle_t(handle.Value);
        if (!SteamUGC.GetQueryUGCResult(nativeHandle, index, out var detail) || detail.m_eResult != EResult.k_EResultOK)
        {
            item = default!;
            return false;
        }

        var children = new PublishedFileId_t[detail.m_unNumChildren];
        if (children.Length > 0 && !SteamUGC.GetQueryUGCChildren(nativeHandle, index, children, (uint)children.Length))
        {
            item = default!;
            return false;
        }

        var previewUrl = SteamUGC.GetQueryUGCPreviewURL(nativeHandle, index, out var value, PreviewUrlBufferSize) ? value : null;
        item = new SteamWorkshopSnapshot(
            detail.m_nPublishedFileId.m_PublishedFileId,
            detail.m_rgchTitle,
            children.Select(child => child.m_PublishedFileId).ToArray(),
            detail.m_rgchDescription,
            detail.m_ulSteamIDOwner,
            detail.m_rgchTags,
            detail.m_bTagsTruncated,
            detail.m_rtimeCreated,
            detail.m_rtimeUpdated,
            detail.m_rtimeAddedToUserList,
            previewUrl);
        return true;
    }

    public bool ReleaseQuery(SteamQueryHandle handle) =>
        SteamUGC.ReleaseQueryUGCRequest(new UGCQueryHandle_t(handle.Value));

    public IReadOnlyList<ulong> GetSubscribedItems()
    {
        var nativeIds = new PublishedFileId_t[SteamUGC.GetNumSubscribedItems()];
        var count = SteamUGC.GetSubscribedItems(nativeIds, (uint)nativeIds.Length);
        return nativeIds.Take((int)Math.Min(count, (uint)nativeIds.Length)).Select(id => id.m_PublishedFileId).ToArray();
    }

    public uint GetItemState(ulong id) => SteamUGC.GetItemState(new PublishedFileId_t(id));

    public bool TryGetInstallInfo(ulong id, out SteamInstallSnapshot install)
    {
        if (SteamUGC.GetItemInstallInfo(new PublishedFileId_t(id), out var size, out var folder, 4096, out var timestamp))
        {
            install = new SteamInstallSnapshot(size, folder, timestamp);
            return true;
        }

        install = default!;
        return false;
    }

    public bool TryGetDownloadInfo(ulong id, out SteamDownloadSnapshot download)
    {
        if (SteamUGC.GetItemDownloadInfo(new PublishedFileId_t(id), out var downloaded, out var total))
        {
            download = new SteamDownloadSnapshot(downloaded, total);
            return true;
        }

        download = default!;
        return false;
    }

    public bool DownloadItem(ulong id, bool highPriority) => SteamUGC.DownloadItem(new PublishedFileId_t(id), highPriority);
    public SteamAsyncCall SubscribeItem(ulong id) => new(SteamUGC.SubscribeItem(new PublishedFileId_t(id)).m_SteamAPICall);
    public SteamAsyncCall UnsubscribeItem(ulong id) => new(SteamUGC.UnsubscribeItem(new PublishedFileId_t(id)).m_SteamAPICall);
}
