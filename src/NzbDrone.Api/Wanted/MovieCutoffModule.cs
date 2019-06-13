using System.Linq;
using NzbDrone.Api.Movies;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Datastore;
using NzbDrone.SignalR;
using Radarr.Http;

namespace NzbDrone.Api.Wanted
{
    public class MovieCutoffModule : MovieModuleWithSignalR
    {
        private readonly IMovieCutoffService _movieCutoffService;

        public MovieCutoffModule(IMovieCutoffService movieCutoffService,
                                 IMovieService movieService,
                                 IQualityUpgradableSpecification qualityUpgradableSpecification,
                                 IBroadcastSignalRMessage signalRBroadcaster)
            : base(movieService, qualityUpgradableSpecification, signalRBroadcaster, "wanted/cutoff")
        {
            _movieCutoffService = movieCutoffService;
            GetResourcePaged = GetCutoffUnmetMovies;
        }

        private PagingResource<MovieResource> GetCutoffUnmetMovies(PagingResource<MovieResource> pagingResource)
        {
            var pagingSpec = pagingResource.MapToPagingSpec<MovieResource, Movie>("title", SortDirection.Ascending);

            var filter = pagingResource.Filters.FirstOrDefault(f => f.Key == "monitored");

            if (filter != null && filter.Value == "false")
            {
                pagingSpec.FilterExpressions.Add(v => v.Monitored == false);
            }
            else
            {
                pagingSpec.FilterExpressions.Add(v => v.Monitored == true);
            }

            var resource = ApplyToPage(_movieCutoffService.MoviesWhereCutoffUnmet, pagingSpec, v => MapToResource(v, true));

            return resource;
        }
    }
}