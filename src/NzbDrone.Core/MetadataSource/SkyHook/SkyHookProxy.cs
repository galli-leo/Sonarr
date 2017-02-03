﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.IO;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.PreDB;
using NzbDrone.Core.Tv;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Profiles;

namespace NzbDrone.Core.MetadataSource.SkyHook
{
    public class SkyHookProxy : IProvideSeriesInfo, ISearchForNewSeries, IProvideMovieInfo, ISearchForNewMovie
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        private readonly IHttpRequestBuilderFactory _requestBuilder;
        private readonly IHttpRequestBuilderFactory _movieBuilder;
        private readonly ITmdbConfigService _configService;
        private readonly IMovieService _movieService;
        private readonly IPreDBService _predbService;

        public SkyHookProxy(IHttpClient httpClient, ISonarrCloudRequestBuilder requestBuilder, ITmdbConfigService configService, IMovieService movieService, IPreDBService predbService, Logger logger)
        {
            _httpClient = httpClient;
             _requestBuilder = requestBuilder.SkyHookTvdb;
            _movieBuilder = requestBuilder.TMDB;
            _configService = configService;
            _movieService = movieService;
            _predbService = predbService;
            _logger = logger;
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(int tvdbSeriesId)
        {
            var httpRequest = _requestBuilder.Create()
                                             .SetSegment("route", "shows")
                                             .Resource(tvdbSeriesId.ToString())
                                             .Build();

            httpRequest.AllowAutoRedirect = true;
            httpRequest.SuppressHttpError = true;

            var httpResponse = _httpClient.Get<ShowResource>(httpRequest);

            if (httpResponse.HasHttpError)
            {
                if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new SeriesNotFoundException(tvdbSeriesId);
                }
                else
                {
                    throw new HttpException(httpRequest, httpResponse);
                }
            }

            var episodes = httpResponse.Resource.Episodes.Select(MapEpisode);
            var series = MapSeries(httpResponse.Resource);

            return new Tuple<Series, List<Episode>>(series, episodes.ToList());
        }

        public Movie GetMovieInfo(int TmdbId, Profile profile = null, bool hasPreDBEntry = false)
        {
            var langCode = profile != null ? IsoLanguages.Get(profile.Language).TwoLetterCode : "us";

            var request = _movieBuilder.Create()
               .SetSegment("route", "movie")
               .SetSegment("id", TmdbId.ToString())
               .SetSegment("secondaryRoute", "")
               .AddQueryParam("append_to_response", "alternative_titles,release_dates,videos")
               .AddQueryParam("language", langCode.ToUpper())
               // .AddQueryParam("country", "US")
               .Build();

            request.AllowAutoRedirect = true;
            request.SuppressHttpError = true;

            var response = _httpClient.Get<MovieResourceRoot>(request);

            // The dude abides, so should us, Lets be nice to TMDb
            // var allowed = int.Parse(response.Headers.GetValues("X-RateLimit-Limit").First()); // get allowed
            // var reset = long.Parse(response.Headers.GetValues("X-RateLimit-Reset").First()); // get time when it resets
            var remaining = int.Parse(response.Headers.GetValues("X-RateLimit-Remaining").First());
            if (remaining <= 5)
            {
                _logger.Trace("Waiting 5 seconds to get information for the next 35 movies");
                Thread.Sleep(5000);
            }

            var resource = response.Resource;
            if (resource.status_message != null)
            {
                if (resource.status_code == 34)
                {
                    _logger.Warn("Movie with TmdbId {0} could not be found. This is probably the case when the movie was deleted from TMDB.", TmdbId);
                    return null;
                }

                _logger.Warn(resource.status_message);
                return null;
            }

			var movie = new Movie();

			if (langCode != "us")
			{
				movie.AlternativeTitles.Add(resource.original_title);
			}

            foreach (var alternativeTitle in resource.alternative_titles.titles)
            {
                if (alternativeTitle.iso_3166_1.ToLower() == langCode)
                {
                    movie.AlternativeTitles.Add(alternativeTitle.title);
                }
                else if (alternativeTitle.iso_3166_1.ToLower() == "us")
                {
                    movie.AlternativeTitles.Add(alternativeTitle.title);
                }
            }

            movie.TmdbId = TmdbId;
            movie.ImdbId = resource.imdb_id;
            movie.Title = resource.title;
            movie.TitleSlug = Parser.Parser.ToUrlSlug(resource.title);
            movie.CleanTitle = Parser.Parser.CleanSeriesTitle(resource.title);
            movie.SortTitle = Parser.Parser.NormalizeTitle(resource.title);
            movie.Overview = resource.overview;
            movie.Website = resource.homepage;

            if (resource.release_date.IsNotNullOrWhiteSpace())
            {
                movie.InCinemas = DateTime.Parse(resource.release_date);

                // get the lowest year in all release date
                var lowestYear = new List<int>();
                foreach (ReleaseDates releaseDates in resource.release_dates.results)
                {
                    foreach (ReleaseDate releaseDate in releaseDates.release_dates)
                    {
                        lowestYear.Add(DateTime.Parse(releaseDate.release_date).Year);
                    }
                }
                movie.Year = lowestYear.Min();
            }

            movie.TitleSlug += "-" + movie.TmdbId.ToString();

            movie.Images.Add(_configService.GetCoverForURL(resource.poster_path, MediaCoverTypes.Poster));//TODO: Update to load image specs from tmdb page!
            movie.Images.Add(_configService.GetCoverForURL(resource.backdrop_path, MediaCoverTypes.Banner));
            movie.Runtime = resource.runtime;

            //foreach(Title title in resource.alternative_titles.titles)
            //{
            //    movie.AlternativeTitles.Add(title.title);
            //}

            foreach(ReleaseDates releaseDates in resource.release_dates.results)
            {
                foreach(ReleaseDate releaseDate in releaseDates.release_dates)
                {
                    if (releaseDate.type == 5 || releaseDate.type == 4)
                    {
                        if (movie.PhysicalRelease.HasValue)
                        {
                            if (movie.PhysicalRelease.Value.After(DateTime.Parse(releaseDate.release_date)))
                            {
                                movie.PhysicalRelease = DateTime.Parse(releaseDate.release_date); //Use oldest release date available.
                            }
                        }
                        else
                        {
                            movie.PhysicalRelease = DateTime.Parse(releaseDate.release_date);
                        }
                    }
                }
            }

            movie.Ratings = new Ratings();
            movie.Ratings.Votes = resource.vote_count;
            movie.Ratings.Value = (decimal)resource.vote_average;

            foreach(Genre genre in resource.genres)
            {
                movie.Genres.Add(genre.name);
            }

            //this is the way it should be handled
            //but unfortunately it seems
            //tmdb lacks alot of release date info
            //omdbapi is actually quite good for this info
            //except omdbapi has been having problems recently
            //so i will just leave this in as a comment
            //and use the 3 month logic that we were using before           
            /*var now = DateTime.Now;
            if (now < movie.InCinemas)
                movie.Status = MovieStatusType.Announced;
            if (now >= movie.InCinemas) 
                movie.Status = MovieStatusType.InCinemas;
            if (now >= movie.PhysicalRelease)
                movie.Status = MovieStatusType.Released;
            */

            
            var now = DateTime.Now;
            //handle the case when we have both theatrical and physical release dates
            if (movie.InCinemas.HasValue && movie.PhysicalRelease.HasValue)
            {
                if (now < movie.InCinemas)
                    movie.Status = MovieStatusType.Announced;
                else if (now >= movie.InCinemas)
                    movie.Status = MovieStatusType.InCinemas;
                if (now >= movie.PhysicalRelease)
                    movie.Status = MovieStatusType.Released;
            }
            //handle the case when we have theatrical release dates but we dont know the physical release date
            else if (movie.InCinemas.HasValue && (now >= movie.InCinemas))
            {
                movie.Status = MovieStatusType.InCinemas;
            }
            //handle the case where we only have a physical release date
            else if (movie.PhysicalRelease.HasValue && (now >= movie.PhysicalRelease))
            {
                movie.Status = MovieStatusType.Released;
            }
            //otherwise the title has only been announced
            else
            {
				movie.Status = MovieStatusType.Announced;
            }

            //since TMDB lacks alot of information lets assume that stuff is released if its been in cinemas for longer than 3 months.
            if (!movie.PhysicalRelease.HasValue && (movie.Status == MovieStatusType.InCinemas) && (((DateTime.Now).Subtract(movie.InCinemas.Value)).TotalSeconds > 60*60*24*30*3))
            {
                movie.Status = MovieStatusType.Released;
            }

			if (!hasPreDBEntry)
			{ 
				if (_predbService.HasReleases(movie))
				{
					movie.HasPreDBEntry = true;
				}
				else
				{
					movie.HasPreDBEntry = false;
				}
			}

            //this matches with the old behavior before the creation of the MovieStatusType.InCinemas
            /*if (resource.status == "Released")
            {
                if (movie.InCinemas.HasValue && (((DateTime.Now).Subtract(movie.InCinemas.Value)).TotalSeconds <= 60 * 60 * 24 * 30 * 3))
                {
                    movie.Status = MovieStatusType.InCinemas;
                }
                else
                {
                    movie.Status = MovieStatusType.Released;
                }
            }
            else
            {
                movie.Status = MovieStatusType.Announced;
            }*/

            if (resource.videos != null)
            {
                foreach (Video video in resource.videos.results)
                {
                    if (video.type == "Trailer" && video.site == "YouTube")
                    {
                        if (video.key != null)
                        {
                            movie.YouTubeTrailerId = video.key;
                            break;
                        }
                    }
                }
            }

            if (resource.production_companies != null)
            {
                if (resource.production_companies.Any())
                {
                    movie.Studio = resource.production_companies[0].name;
                }
            }
            movie.AllFlicksTitle = null;

            //should be able to grab a bool from enableAllFlicks/EnableAllFlicks under Settings-->UI
            bool enableAllFlicks = true;
            if (enableAllFlicks)
            {

                //specify netflix region by its country code
                //should be able to grab this from the netflixCountryCode/NetflixCountryCode under Settings-->UI
                //string countryCode = "us";
                string countryCode = "ca";

                string referer;
                if (countryCode == "ca")
                    referer = "https://www.allflicks.net/canada/";
                else if (countryCode == "us")
                    referer = "https://www.allflicks.net/";
                else
                    referer = "https://www.allflicks.net/";
                int now_year = DateTime.Now.Year;

                string url = "https://www.allflicks.net/wp-content/themes/responsive/processing/processing_" + countryCode + ".php";

                string cookIdent;
                using (WebClient client = new WebClient())
                {
                    string htmlCode = client.DownloadString("https://allflicks.net");
                    htmlCode = htmlCode.Replace(" ", String.Empty);
                    cookIdent = "identifier=" + getBetween(htmlCode, "document.cookie=\"identifier=", "\"+expires+\";path=/;domain=.allflicks.net\"");
                    //Console.WriteLine(cookIdent);
                }

                int length = 100;
                int start = 0;
                string titleForNetflix = movie.Title;
                int numFound = 1;
                while (start < numFound && !(movie.Year > now_year))
                {
                    string postData = "draw=4&columns[0][data]=box_art&columns[0][name]=&columns[0][searchable]=true&columns[0][orderable]=false&columns[0][search][value]=&columns[0][search][regex]=false&columns[1][data]=title&columns[1][name]=&columns[1][searchable]=true&columns[1][orderable]=true&columns[1][search][value]=&columns[1][search][regex]=false&columns[2][data]=year&columns[2][name]=&columns[2][searchable]=true&columns[2][orderable]=true&columns[2][search][value]=&columns[2][search][regex]=false&columns[3][data]=genre&columns[3][name]=&columns[3][searchable]=true&columns[3][orderable]=true&columns[3][search][value]=&columns[3][search][regex]=false&columns[4][data]=rating&columns[4][name]=&columns[4][searchable]=true&columns[4][orderable]=true&columns[4][search][value]=&columns[4][search][regex]=false&columns[5][data]=available&columns[5][name]=&columns[5][searchable]=true&columns[5][orderable]=true&columns[5][search][value]=&columns[5][search][regex]=false&columns[6][data]=director&columns[6][name]=&columns[6][searchable]=true&columns[6][orderable]=true&columns[6][search][value]=&columns[6][search][regex]=false&columns[7][data]=cast&columns[7][name]=&columns[7][searchable]=true&columns[7][orderable]=true&columns[7][search][value]=&columns[7][search][regex]=false&order[0][column]=5&order[0][dir]=desc&start=" + start.ToString() + "&length=" + length.ToString() + "&search[value]=" + titleForNetflix + "&search[regex]=false&movies=true&shows=false&documentaries=true&rating=netflix&min=1900&max=" + now_year.ToString();
                    byte[] byteArray = Encoding.UTF8.GetBytes(postData);

                    HttpWebRequest rquest = (HttpWebRequest)WebRequest.Create(url);
                    rquest.Method = "POST";
                    rquest.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:51.0) Gecko/20100101 Firefox/51.0";
                    rquest.Accept = "application.json, text/javascript, */*; q=0.01";
                    rquest.Headers.Add("X-Requested-With", "XMLHttpRequest");
                    rquest.Referer = referer;
                    rquest.Headers.Add("Cookie", cookIdent);
                    rquest.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                    rquest.Host = "www.allflicks.net";
                    using (var st = rquest.GetRequestStream())
                        st.Write(byteArray, 0, byteArray.Length);

                    var rsponse = (HttpWebResponse)rquest.GetResponse();
                    //Console.WriteLine((rsponse).StatusDescription);

                    var rsponseString = new StreamReader(rsponse.GetResponseStream()).ReadToEnd();
                    rsponse.Close();
                    //Console.WriteLine(rsponseString);
                    dynamic j1 = JObject.Parse(rsponseString);

                    numFound = j1.recordsFiltered;
                    if (!(numFound > 0))
                        break;
                    var results = j1.data;
                    //Console.WriteLine(results);
                    foreach (var result in results)
                    {
                        //Console.WriteLine(result);
                        if (result.title.ToString().ToUpper() == titleForNetflix.ToUpper())
                        {
                            if ((Convert.ToInt32(result.year) <= movie.Year + 1) && (Convert.ToInt32(result.year) >= movie.Year - 1))
                            {
                                movie.AllFlicksTitle = result.slug;
                                //Console.WriteLine(movie.AllFlicksTitle);
                            }
                        }
                    }
                    //Console.WriteLine("numFound=" + numFound.ToString());
                    start = start + length;
                }
            }

            return movie;
        }

        public Movie GetMovieInfo(string ImdbId)
        {
            var request = _movieBuilder.Create()
                .SetSegment("route", "find")
                .SetSegment("id", ImdbId)
                .SetSegment("secondaryRoute", "")
                .AddQueryParam("external_source", "imdb_id")
                .Build();

            request.AllowAutoRedirect = true;
            request.SuppressHttpError = true;

            var response = _httpClient.Get<FindRoot>(request);

            // The dude abides, so should us, Lets be nice to TMDb
            // var allowed = int.Parse(response.Headers.GetValues("X-RateLimit-Limit").First()); // get allowed
            // var reset = long.Parse(response.Headers.GetValues("X-RateLimit-Reset").First()); // get time when it resets
            var remaining = int.Parse(response.Headers.GetValues("X-RateLimit-Remaining").First());
            if (remaining <= 5)
            {
                _logger.Trace("Waiting 5 seconds to get information for the next 35 movies");
                Thread.Sleep(5000);
            }

            var resources = response.Resource;

            return resources.movie_results.SelectList(MapMovie).FirstOrDefault();
        }

        private string StripTrailingTheFromTitle(string title)
        {
            if(title.EndsWith(",the"))
            {
                title = title.Substring(0, title.Length - 4);
            } else if(title.EndsWith(", the"))
            {
                title = title.Substring(0, title.Length - 5);
            }
            return title;
        }

        public List<Movie> SearchForNewMovie(string title)
        {
            var lowerTitle = title.ToLower();

            lowerTitle = lowerTitle.Replace(".", "");

            var parserResult = Parser.Parser.ParseMovieTitle(title, true);

            var yearTerm = "";

            if (parserResult != null && parserResult.MovieTitle != title)
            {
                //Parser found something interesting!
                lowerTitle = parserResult.MovieTitle.ToLower().Replace(".", " "); //TODO Update so not every period gets replaced (e.g. R.I.P.D.)
                if (parserResult.Year > 1800)
                {
                    yearTerm = parserResult.Year.ToString();
                }
                
                if (parserResult.ImdbId.IsNotNullOrWhiteSpace())
                {
                    return new List<Movie> { GetMovieInfo(parserResult.ImdbId) };
                }
            }

            lowerTitle = StripTrailingTheFromTitle(lowerTitle);

            if (lowerTitle.StartsWith("imdb:") || lowerTitle.StartsWith("imdbid:"))
            {
                var slug = lowerTitle.Split(':')[1].Trim();

                string imdbid = slug;

                if (slug.IsNullOrWhiteSpace() || slug.Any(char.IsWhiteSpace))
                {
                    return new List<Movie>();
                }

                try
                {
                    return new List<Movie> { GetMovieInfo(imdbid) };
                }
                catch (SeriesNotFoundException)
                {
                    return new List<Movie>();
                }
            }

            var searchTerm = lowerTitle.Replace("_", "+").Replace(" ", "+").Replace(".", "+");

            var firstChar = searchTerm.First();

            var request = _movieBuilder.Create()
                .SetSegment("route", "search")
                .SetSegment("id", "movie")
                .SetSegment("secondaryRoute", "")
                .AddQueryParam("query", searchTerm)
                .AddQueryParam("year", yearTerm)
                .AddQueryParam("include_adult", false)
                .Build();

            request.AllowAutoRedirect = true;
            request.SuppressHttpError = true;

            /*var imdbRequest = new HttpRequest("https://v2.sg.media-imdb.com/suggests/" + firstChar + "/" + searchTerm + ".json");

            var response = _httpClient.Get(imdbRequest);

            var imdbCallback = "imdb$" + searchTerm + "(";

            var responseCleaned = response.Content.Replace(imdbCallback, "").TrimEnd(")");

            _logger.Warn("Cleaned response: " + responseCleaned);

            ImdbResource json = JsonConvert.DeserializeObject<ImdbResource>(responseCleaned);

            _logger.Warn("Json object: " + json);

            _logger.Warn("Crash ahead.");*/

            var response = _httpClient.Get<MovieSearchRoot>(request);

            var movieResults = response.Resource.results;

            return movieResults.SelectList(MapMovie);
        }

        public List<Series> SearchForNewSeries(string title)
        {
            try
            {
                var lowerTitle = title.ToLowerInvariant();

                if (lowerTitle.StartsWith("tvdb:") || lowerTitle.StartsWith("tvdbid:"))
                {
                    var slug = lowerTitle.Split(':')[1].Trim();

                    int tvdbId;

                    if (slug.IsNullOrWhiteSpace() || slug.Any(char.IsWhiteSpace) || !int.TryParse(slug, out tvdbId) || tvdbId <= 0)
                    {
                        return new List<Series>();
                    }

                    try
                    {
                        return new List<Series> { GetSeriesInfo(tvdbId).Item1 };
                    }
                    catch (SeriesNotFoundException)
                    {
                        return new List<Series>();
                    }
                }

               

                var httpRequest = _requestBuilder.Create()
                                                 .SetSegment("route", "search")
                                                 .AddQueryParam("term", title.ToLower().Trim())
                                                 .Build();

                

                var httpResponse = _httpClient.Get<List<ShowResource>>(httpRequest);

                return httpResponse.Resource.SelectList(MapSeries);
            }
            catch (HttpException)
            {
                throw new SkyHookException("Search for '{0}' failed. Unable to communicate with SkyHook.", title);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, ex.Message);
                throw new SkyHookException("Search for '{0}' failed. Invalid response received from SkyHook.", title);
            }
        }

        private Movie MapMovie(MovieResult result)
        {
            var imdbMovie = new Movie();
            imdbMovie.TmdbId = result.id;
            try
            {
                imdbMovie.SortTitle = Parser.Parser.NormalizeTitle(result.title);
                imdbMovie.Title = result.title;
                imdbMovie.TitleSlug = Parser.Parser.ToUrlSlug(result.title);

                if (result.release_date.IsNotNullOrWhiteSpace())
                {
                    imdbMovie.Year = DateTime.Parse(result.release_date).Year;
                }

                imdbMovie.TitleSlug += "-" + imdbMovie.TmdbId;

                imdbMovie.Images = new List<MediaCover.MediaCover>();
                imdbMovie.Overview = result.overview;
                try
                {
                    var imdbPoster = _configService.GetCoverForURL(result.poster_path, MediaCoverTypes.Poster);
                    imdbMovie.Images.Add(imdbPoster);
                }
                catch (Exception e)
                {
                    _logger.Debug(result);
                }

                return imdbMovie;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error occured while searching for new movies.");
            }

            return null;
        }

        private static Series MapSeries(ShowResource show)
        {
            var series = new Series();
            series.TvdbId = show.TvdbId;

            if (show.TvRageId.HasValue)
            {
                series.TvRageId = show.TvRageId.Value;
            }

            if (show.TvMazeId.HasValue)
            {
                series.TvMazeId = show.TvMazeId.Value;
            }

            series.ImdbId = show.ImdbId;
            series.Title = show.Title;
            series.CleanTitle = Parser.Parser.CleanSeriesTitle(show.Title);
            series.SortTitle = SeriesTitleNormalizer.Normalize(show.Title, show.TvdbId);

            if (show.FirstAired != null)
            {
                series.FirstAired = DateTime.Parse(show.FirstAired).ToUniversalTime();
                series.Year = series.FirstAired.Value.Year;
            }

            series.Overview = show.Overview;

            if (show.Runtime != null)
            {
                series.Runtime = show.Runtime.Value;
            }

            series.Network = show.Network;

            if (show.TimeOfDay != null)
            {
                series.AirTime = string.Format("{0:00}:{1:00}", show.TimeOfDay.Hours, show.TimeOfDay.Minutes);
            }

            series.TitleSlug = show.Slug;
            series.Status = MapSeriesStatus(show.Status);
            series.Ratings = MapRatings(show.Rating);
            series.Genres = show.Genres;

            if (show.ContentRating.IsNotNullOrWhiteSpace())
            {
                series.Certification = show.ContentRating.ToUpper();
            }
            
            series.Actors = show.Actors.Select(MapActors).ToList();
            series.Seasons = show.Seasons.Select(MapSeason).ToList();
            series.Images = show.Images.Select(MapImage).ToList();

            return series;
        }

        private static Actor MapActors(ActorResource arg)
        {
            var newActor = new Actor
            {
                Name = arg.Name,
                Character = arg.Character
            };

            if (arg.Image != null)
            {
                newActor.Images = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover(MediaCoverTypes.Headshot, arg.Image)
                };
            }

            return newActor;
        }

        private static Episode MapEpisode(EpisodeResource oracleEpisode)
        {
            var episode = new Episode();
            episode.Overview = oracleEpisode.Overview;
            episode.SeasonNumber = oracleEpisode.SeasonNumber;
            episode.EpisodeNumber = oracleEpisode.EpisodeNumber;
            episode.AbsoluteEpisodeNumber = oracleEpisode.AbsoluteEpisodeNumber;
            episode.Title = oracleEpisode.Title;

            episode.AirDate = oracleEpisode.AirDate;
            episode.AirDateUtc = oracleEpisode.AirDateUtc;

            episode.Ratings = MapRatings(oracleEpisode.Rating);

            //Don't include series fanart images as episode screenshot
            if (oracleEpisode.Image != null)
            {
                episode.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Screenshot, oracleEpisode.Image));
            }

            return episode;
        }

        private static Season MapSeason(SeasonResource seasonResource)
        {
            return new Season
            {
                SeasonNumber = seasonResource.SeasonNumber,
                Images = seasonResource.Images.Select(MapImage).ToList()
            };
        }

        private static SeriesStatusType MapSeriesStatus(string status)
        {
            if (status.Equals("ended", StringComparison.InvariantCultureIgnoreCase))
            {
                return SeriesStatusType.Ended;
            }

            return SeriesStatusType.Continuing;
        }

        private static Ratings MapRatings(RatingResource rating)
        {
            if (rating == null)
            {
                return new Ratings();
            }

            return new Ratings
            {
                Votes = rating.Count,
                Value = rating.Value
            };
        }

        private static MediaCover.MediaCover MapImage(ImageResource arg)
        {
            return new MediaCover.MediaCover
            {
                Url = arg.Url,
                CoverType = MapCoverType(arg.CoverType)
            };
        }

        private static MediaCoverTypes MapCoverType(string coverType)
        {
            switch (coverType.ToLower())
            {
                case "poster":
                    return MediaCoverTypes.Poster;
                case "banner":
                    return MediaCoverTypes.Banner;
                case "fanart":
                    return MediaCoverTypes.Fanart;
                default:
                    return MediaCoverTypes.Unknown;
            }
        }

        public static string getBetween(string strSource, string strStart, string strEnd)
        {
            int Start, End;
            if (strSource.Contains(strStart) && strSource.Contains(strEnd))
            {
                Start = strSource.IndexOf(strStart, 0) + strStart.Length;
                End = strSource.IndexOf(strEnd, Start);
                return strSource.Substring(Start, End - Start);
            }
            else
            {
                return "";
            }
        }

        public static string ToUrlSlug(string value)
        {
            //First to lower case
            value = value.ToLowerInvariant();

            //Remove all accents
            var bytes = Encoding.GetEncoding("ISO-8859-8").GetBytes(value);
            value = Encoding.ASCII.GetString(bytes);

            //Replace spaces
            value = Regex.Replace(value, @"\s", "-", RegexOptions.Compiled);

            //Remove invalid chars
            value = Regex.Replace(value, @"[^a-z0-9\s-_]", "", RegexOptions.Compiled);

            //Trim dashes from end
            value = value.Trim('-', '_');

            //Replace double occurences of - or _
            value = Regex.Replace(value, @"([-_]){2,}", "$1", RegexOptions.Compiled);

            return value;
        }

        public Movie MapMovieToTmdbMovie(Movie movie)
        {
            Movie newMovie = movie;
            if (movie.TmdbId > 0)
            {
                newMovie = GetMovieInfo(movie.TmdbId);
            }
            else if (movie.ImdbId.IsNotNullOrWhiteSpace())
            {
                newMovie = GetMovieInfo(movie.ImdbId);
            }
            else
            {
                var yearStr = "";
                if (movie.Year > 1900)
                {
                    yearStr = $" {movie.Year}";
                }
                newMovie = SearchForNewMovie(movie.Title + yearStr).FirstOrDefault();
            }

            if (newMovie == null)
            {
                _logger.Warn("Couldn't map movie {0} to a movie on The Movie DB. It will not be added :(", movie.Title);
                return null;
            }

            newMovie.Path = movie.Path;
            newMovie.RootFolderPath = movie.RootFolderPath;
            newMovie.ProfileId = movie.ProfileId;
            newMovie.Monitored = movie.Monitored;
            newMovie.MovieFile = movie.MovieFile;
            newMovie.MinimumAvailability = movie.MinimumAvailability;

            return newMovie;
        }
    }
}
