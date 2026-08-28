using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// Turns remote listings into placeholders: real directory entries with the right name, size and
/// dates, whose bytes stay in the bucket until <see cref="Hydrator"/> is asked for them.
/// </summary>
public static unsafe class Placeholders
{
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    public static void Create(string directory, IEnumerable<RemoteItem> items)
    {
        // One placeholder per call: the create-info wants pinned name and identity strings, and
        // pinning a batch of them buys nothing at portal folder sizes.
        foreach (var item in items) CreateOne(directory, item);
    }

    static void CreateOne(string directory, RemoteItem item)
    {
        long filetime = item.Modified.ToFileTime();

        fixed (char* dir = directory)
        fixed (char* name = item.Name)
        fixed (char* identity = item.Id)
        {
            var info = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = name,
                // The identity blob is the UTF-16 bytes of the row id — what FETCH_DATA gets
                // handed back, and all it needs to name the file to the portal.
                FileIdentity = identity,
                FileIdentityLength = (uint)(item.Id.Length * sizeof(char)),
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
            };

            info.FsMetadata.FileSize = item.IsFolder ? 0 : item.Size;
            info.FsMetadata.BasicInfo.CreationTime = filetime;
            info.FsMetadata.BasicInfo.LastWriteTime = filetime;
            info.FsMetadata.BasicInfo.LastAccessTime = filetime;
            info.FsMetadata.BasicInfo.ChangeTime = filetime;
            info.FsMetadata.BasicInfo.FileAttributes = item.IsFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

            if (item.IsFolder)
            {
                // The engine fills folders itself (population is always-full), so the filter must
                // not hold this one empty waiting for an on-demand population that never comes.
                info.Flags |= CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION;
            }

            uint processed;
            PInvoke.CfCreatePlaceholders(dir, &info, 1,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed).ThrowOnFailure();
            info.Result.ThrowOnFailure();
        }
    }
}
