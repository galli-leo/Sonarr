using System.Linq;
using Nancy;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using Readarr.Http.Extensions;

namespace Readarr.Api.V1.FileSystem
{
    public class FileSystemModule : ReadarrV1Module
    {
        private readonly IFileSystemLookupService _fileSystemLookupService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;

        public FileSystemModule(IFileSystemLookupService fileSystemLookupService,
                                IDiskProvider diskProvider,
                                IDiskScanService diskScanService)
            : base("/filesystem")
        {
            _fileSystemLookupService = fileSystemLookupService;
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;
            Get("/", x => GetContents());
            Get("/type", x => GetEntityType());
            Get("/mediafiles", x => GetMediaFiles());
        }

        private object GetContents()
        {
            var pathQuery = Request.Query.path;
            var includeFiles = Request.GetBooleanQueryParameter("includeFiles");
            var allowFoldersWithoutTrailingSlashes = Request.GetBooleanQueryParameter("allowFoldersWithoutTrailingSlashes");

            return _fileSystemLookupService.LookupContents((string)pathQuery.Value, includeFiles, allowFoldersWithoutTrailingSlashes);
        }

        private object GetEntityType()
        {
            var pathQuery = Request.Query.path;
            var path = (string)pathQuery.Value;

            if (_diskProvider.FileExists(path))
            {
                return new { type = "file" };
            }

            //Return folder even if it doesn't exist on disk to avoid leaking anything from the UI about the underlying system
            return new { type = "folder" };
        }

        private object GetMediaFiles()
        {
            var pathQuery = Request.Query.path;
            var path = (string)pathQuery.Value;

            if (!_diskProvider.FolderExists(path))
            {
                return new string[0];
            }

            return _diskScanService.GetBookFiles(path).Select(f => new
            {
                Path = f.FullName,
                Name = f.Name
            });
        }
    }
}