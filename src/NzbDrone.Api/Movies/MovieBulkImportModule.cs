using System.Collections;
using System.Collections.Generic;
using Nancy;
using NzbDrone.Api.Extensions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using System.Linq;
using System;
using Marr.Data;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.RootFolders;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Tv;
using NzbDrone.Common.Disk;
using System.IO;

namespace NzbDrone.Api.Movie
{

    public class UnmappedComparer : IComparer<UnmappedFolder>
    {
        public int Compare(UnmappedFolder a, UnmappedFolder b)
        {
            return a.Name.CompareTo(b.Name);
        }
    }

    public class MovieBulkImportModule : NzbDroneRestModule<MovieResource>
    {
        private readonly ISearchForNewMovie _searchProxy;
        private readonly IRootFolderService _rootFolderService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;
		private readonly ICached<Core.Tv.Movie> _mappedMovies;
        private readonly IMovieService _movieService;

        public MovieBulkImportModule(ISearchForNewMovie searchProxy, IRootFolderService rootFolderService, IMakeImportDecision importDecisionMaker,
                                    IDiskProvider diskProvider, IDiskScanService diskScanService, ICacheManager cacheManager, IMovieService movieService)
            : base("/movies/bulkimport")
        {
            _searchProxy = searchProxy;
            _rootFolderService = rootFolderService;
            _importDecisionMaker = importDecisionMaker;
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;

			_mappedMovies = cacheManager.GetCache<Core.Tv.Movie>(GetType(), "mappedMoviesCache");
            _movieService = movieService;
            Get["/"] = x => Search();
        }


        private Response Search()
        {
            bool flatFiles = true;

            if (Request.Query.Id == 0)
            {
                //Todo error handling
            }

            RootFolder rootFolder = _rootFolderService.Get(Request.Query.Id);

            int page = Request.Query.page;
            int per_page = Request.Query.per_page;

            int min = (page - 1) * per_page;

            int max = page * per_page;

            var unmappedDirectories = rootFolder.UnmappedFolders.OrderBy(f => f.Name).ToList();
            var unmappedFiles = new List<UnmappedFile>();

            if (flatFiles)
            {
                unmappedFiles = rootFolder.UnmappedFiles.OrderBy(f => f.Name).ToList();
            }

            var unmapped = unmappedDirectories.ToDictionary(o => o.Name, o => o.Path).Concat(unmappedFiles.ToDictionary(o => o.Name, o => o.Path)).ToList();

            int total_count = unmapped.Count;

            if (Request.Query.total_entries.HasValue)
            {
                total_count = Request.Query.total_entries;
            }

            max = total_count >= max ? max : total_count;

            var paged = unmapped.GetRange(min, max-min);

            var mapped = paged.Select(f =>
			{
				Core.Tv.Movie m = null;

				var mappedMovie = _mappedMovies.Find(f.Key);

				if (mappedMovie != null)
				{
					return mappedMovie;
				}

				var parsedTitle = Parser.ParseMoviePath(f.Key, false);
				if (parsedTitle == null)
				{
					m = new Core.Tv.Movie
					{
						Title = f.Key.Replace(".", " ").Replace("-", " "),
						Path = f.Value,
					};
				}
				else
				{
					m = new Core.Tv.Movie
					{
						Title = parsedTitle.MovieTitle,
						Year = parsedTitle.Year,
						ImdbId = parsedTitle.ImdbId,
						Path = f.Value
					};
				}

				var files = _diskProvider.FileExists(f.Value) ? new string[] { f.Value } : _diskScanService.GetVideoFiles(f.Value);

				var decisions = _importDecisionMaker.GetImportDecisions(files.ToList(), m, true);

				var decision = decisions.Where(d => d.Approved && !d.Rejections.Any()).FirstOrDefault();

				if (decision != null)
				{
					var local = decision.LocalMovie;

					m.MovieFile = new LazyLoaded<MovieFile>(new MovieFile
					{
						Path = local.Path,
						Edition = local.ParsedMovieInfo.Edition,
						Quality = local.Quality,
						MediaInfo = local.MediaInfo,
						ReleaseGroup = local.ParsedMovieInfo.ReleaseGroup,
						RelativePath = f.Value.GetRelativePath(local.Path)
					});
				}

				mappedMovie = _searchProxy.MapMovieToTmdbMovie(m);
                
				if (mappedMovie != null)
				{
                    if (_diskProvider.FileExists(mappedMovie.Path))
                    {
                        mappedMovie.Path = Path.GetDirectoryName(mappedMovie.Path);
                    }
                    
					mappedMovie.Monitored = true;

					_mappedMovies.Set(f.Key, mappedMovie, TimeSpan.FromDays(2));

					return mappedMovie;
				}

				return null;
            });
            
            return new PagingResource<MovieResource>
            {
                Page = page,
                PageSize = per_page,
                SortDirection = SortDirection.Ascending,
                SortKey = Request.Query.sort_by,
                TotalRecords = total_count - mapped.Where(m => m == null).Count(),
                Records = MapToResource(mapped.Where(m => m != null)).ToList()
            }.AsResponse();
        }


        private static IEnumerable<MovieResource> MapToResource(IEnumerable<Core.Tv.Movie> movies)
        {
            foreach (var currentMovie in movies)
            {
                var resource = currentMovie.ToResource();
                var poster = currentMovie.Images.FirstOrDefault(c => c.CoverType == MediaCoverTypes.Poster);
                if (poster != null)
                {
                    resource.RemotePoster = poster.Url;
                }

                yield return resource;
            }
        }
    }
}
